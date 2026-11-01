using System.Text;
using JellyShare.Application.Features.Placeholders;
using JellyShare.Data.Files.Placeholders;
using JellyShare.Data.Files.Stores;
using JellyShare.Domain.Exceptions;
using JellyShare.Domain.Models.Placeholders;
using JellyShare.UnitTests.Fakes;

namespace JellyShare.UnitTests.Storage;

public sealed class PlaceholderWriterTests
{
    private const string MovieDir = "Sintel (2010) [tmdbid-45745]";

    private static PlaceholderDiff Diff(IEnumerable<FileWrite>? writes = null, IEnumerable<string>? deletes = null)
    {
        var w = (writes ?? []).ToList();
        var d = (deletes ?? []).ToList();
        var dirs = w.Select(x => x.RelativePath).Concat(d)
            .Select(p => p.Contains('/', StringComparison.Ordinal) ? p[..p.LastIndexOf('/')] : string.Empty)
            .Distinct().Order(StringComparer.Ordinal).ToList();
        return new PlaceholderDiff(w, d, dirs);
    }

    private static FileWrite Movie(string ext, string content = "x")
        => new($"{MovieDir}/Sintel (2010).{ext}", content);

    [Fact]
    public async Task Writer_RefusesPathsOutsideRoot()
    {
        using var tmp = new TempDir();
        var root = tmp.Sub("root");
        var outside = tmp.Sub("evil.strm");
        var diff = Diff(
            [
                new FileWrite("../evil.strm", "pwn"),
                new FileWrite("a/../../evil.strm", "pwn"),
                new FileWrite(outside.Replace('\\', '/'), "pwn"),
                new FileWrite("a\\..\\..\\evil.strm", "pwn"),
                new FileWrite("/tmp/evil.strm", "pwn"),
                new FileWrite("top.strm", "pwn"),
                new FileWrite("dir/payload.exe", "pwn"),
                Movie("strm"),
            ],
            ["../evil.strm", ".jellyshare"]);
        File.WriteAllText(outside, "keep");

        var result = await new PlaceholderWriter().ApplyAsync(diff, root, null, CancellationToken.None);

        File.ReadAllText(outside).ShouldBe("keep");
        result.Refused.ShouldBe(9);
        result.Written.ShouldBe(1);
        File.Exists(Path.Combine(root, MovieDir, "Sintel (2010).strm")).ShouldBeTrue();
        File.Exists(Path.Combine(root, PlaceholderWriter.MarkerName)).ShouldBeTrue();
        File.Exists(Path.Combine(root, "dir", "payload.exe")).ShouldBeFalse();
        foreach (var d in result.ChangedDirs)
        {
            d.ShouldStartWith(Path.GetFullPath(root), Case.Sensitive);
        }
    }

    [Fact]
    public async Task Writer_IsAtomic_NoPartialStrm()
    {
        using var tmp = new TempDir();
        var root = tmp.Sub("root");
        var target = Path.Combine(root, MovieDir, "Sintel (2010).strm");
        await new PlaceholderWriter().ApplyAsync(Diff([Movie("strm", "old")]), root, null, CancellationToken.None);

        var seenDuringWrite = new List<string>();
        var writer = new PlaceholderWriter
        {
            BeforeCommit = (tempPath, finalPath) =>
            {
                seenDuringWrite.Add(File.ReadAllText(finalPath));
                File.ReadAllText(tempPath).ShouldBe("new content");
                throw new IOException("simulated crash before commit");
            },
        };

        var ex = await Should.ThrowAsync<PlaceholderWriteException>(() => writer.ApplyAsync(Diff([Movie("strm", "new content")]), root, null, CancellationToken.None));
        ex.InnerException.ShouldBeOfType<IOException>();

        seenDuringWrite.ShouldBe(["old"]);
        File.ReadAllText(target).ShouldBe("old");
        Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).ShouldBeEmpty();

        await new PlaceholderWriter().ApplyAsync(Diff([Movie("strm", "new content")]), root, null, CancellationToken.None);
        File.ReadAllText(target).ShouldBe("new content");
        Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public async Task Writer_RemovesEmptyDirectories()
    {
        using var tmp = new TempDir();
        var root = tmp.Sub("root");
        var series = "Pioneer One [tvdbid-153021]";
        await new PlaceholderWriter().ApplyAsync(
            Diff(
            [
                new FileWrite($"{series}/tvshow.nfo", "s"),
                new FileWrite($"{series}/Season 01/Pioneer One S01E02.strm", "a"),
                new FileWrite($"{series}/Season 02/Pioneer One S02E01.strm", "b"),
                new FileWrite($"{series}/Season 02/Pioneer One S02E01.nfo", "c"),
            ]),
            root,
            null,
            CancellationToken.None);

        var result = await new PlaceholderWriter().ApplyAsync(
            Diff(deletes: [$"{series}/Season 02/Pioneer One S02E01.nfo", $"{series}/Season 02/Pioneer One S02E01.strm"]),
            root,
            null,
            CancellationToken.None);

        result.Deleted.ShouldBe(2);
        Directory.Exists(Path.Combine(root, series, "Season 02")).ShouldBeFalse();
        File.Exists(Path.Combine(root, series, "Season 01", "Pioneer One S01E02.strm")).ShouldBeTrue();

        await new PlaceholderWriter().ApplyAsync(
            Diff(deletes: [$"{series}/Season 01/Pioneer One S01E02.strm", $"{series}/tvshow.nfo"]),
            root,
            null,
            CancellationToken.None);

        Directory.Exists(Path.Combine(root, series)).ShouldBeFalse();
        Directory.Exists(root).ShouldBeTrue();
        File.Exists(Path.Combine(root, PlaceholderWriter.MarkerName)).ShouldBeTrue();
    }

    [Fact]
    public async Task Writer_CreatesMarker_AndNeverDeletesIt()
    {
        using var tmp = new TempDir();
        var root = tmp.Sub("root");
        await new PlaceholderWriter().ApplyAsync(Diff([Movie("strm")]), root, null, CancellationToken.None);
        File.Exists(Path.Combine(root, PlaceholderWriter.MarkerName)).ShouldBeTrue();

        await new PlaceholderWriter().ApplyAsync(
            Diff(deletes: [$"{MovieDir}/Sintel (2010).strm", PlaceholderWriter.MarkerName]), root, null, CancellationToken.None);

        File.Exists(Path.Combine(root, PlaceholderWriter.MarkerName)).ShouldBeTrue();
        Directory.GetFileSystemEntries(root).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Writer_MovieRemoval_RemovesWholeFolder()
    {
        using var tmp = new TempDir();
        var root = tmp.Sub("root");
        await new PlaceholderWriter().ApplyAsync(
            Diff([Movie("strm"), Movie("nfo")]),
            root,
            (dir, type, _) => Task.FromResult<Stream?>(new MemoryStream(Encoding.UTF8.GetBytes(type))),
            CancellationToken.None);
        File.Exists(Path.Combine(root, MovieDir, "poster.jpg")).ShouldBeTrue();
        File.Exists(Path.Combine(root, MovieDir, "fanart.jpg")).ShouldBeTrue();

        var result = await new PlaceholderWriter().ApplyAsync(
            Diff(deletes: [$"{MovieDir}/Sintel (2010).strm", $"{MovieDir}/Sintel (2010).nfo"]), root, null, CancellationToken.None);

        Directory.Exists(Path.Combine(root, MovieDir)).ShouldBeFalse();
        result.ChangedDirs.ShouldContain(Path.GetFullPath(Path.Combine(root, MovieDir)));
    }

    [Fact]
    public async Task Writer_DownloadsImagesOncePerDir_AndFailuresAreNonFatal()
    {
        using var tmp = new TempDir();
        var root = tmp.Sub("root");
        var series = "Pioneer One [tvdbid-153021]";
        var calls = new List<(string Dir, string Type)>();
        var result = await new PlaceholderWriter().ApplyAsync(
            Diff(
            [
                new FileWrite($"{series}/Season 01/Pioneer One S01E02.strm", "a"),
                new FileWrite($"{series}/Season 02/Pioneer One S02E01.strm", "b"),
                Movie("strm"),
            ]),
            root,
            (dir, type, _) =>
            {
                calls.Add((dir, type));
                if (dir == MovieDir)
                {
                    throw new HttpRequestException("peer down");
                }

                return Task.FromResult<Stream?>(type == "Primary" ? new MemoryStream([1, 2, 3]) : null);
            },
            CancellationToken.None);

        result.Written.ShouldBe(3);
        result.ImagesWritten.ShouldBe(1);
        calls.OrderBy(c => c.Dir == MovieDir ? 0 : 1).ThenBy(c => c.Type == "Primary" ? 0 : 1).ToList().ShouldBe([(MovieDir, "Primary"), (MovieDir, "Backdrop"), (series, "Primary"), (series, "Backdrop")]);
        File.ReadAllBytes(Path.Combine(root, series, "poster.jpg")).ShouldBe([1, 2, 3]);
        File.Exists(Path.Combine(root, series, "fanart.jpg")).ShouldBeFalse();

        calls.Clear();
        await new PlaceholderWriter().ApplyAsync(
            Diff([new FileWrite($"{series}/Season 01/Pioneer One S01E03.strm", "c")]),
            root,
            (dir, type, _) =>
            {
                calls.Add((dir, type));
                return Task.FromResult<Stream?>(null);
            },
            CancellationToken.None);
        calls.ShouldBe([(series, "Backdrop")]);
    }

    [Fact]
    public async Task Writer_Failure_ReportsPartialChangedDirs()
    {
        using var tmp = new TempDir();
        var root = tmp.Sub("root");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, PlaceholderWriter.MarkerName), string.Empty);
        File.WriteAllText(Path.Combine(root, "Blocked"), "file where a folder should be");

        var ex = await Should.ThrowAsync<PlaceholderWriteException>(() => new PlaceholderWriter().ApplyAsync(
            Diff([Movie("strm"), new FileWrite("Blocked/x.strm", "x")]), root, null, CancellationToken.None));

        ex.Partial.Written.ShouldBe(1);
        ex.Partial.ChangedDirs.ShouldBe([Path.GetFullPath(Path.Combine(root, MovieDir))]);
        ex.InnerException.ShouldBeAssignableTo<IOException>();
    }

    [Fact]
    public async Task Writer_Cancelled_ReportsPartialChangedDirs()
    {
        using var tmp = new TempDir();
        var root = tmp.Sub("root");
        using var cts = new CancellationTokenSource();
        var writer = new PlaceholderWriter { BeforeCommit = (_, final) => { if (final.EndsWith(".nfo", StringComparison.Ordinal)) { cts.Cancel(); } } };

        var ex = await Should.ThrowAsync<PlaceholderWriteException>(() => writer.ApplyAsync(
            Diff([Movie("nfo"), Movie("strm"), new FileWrite("Other/o.strm", "o")]), root, null, cts.Token));

        ex.InnerException.ShouldBeAssignableTo<OperationCanceledException>();
        ex.Partial.ChangedDirs.ShouldContain(Path.GetFullPath(Path.Combine(root, MovieDir)));
    }

    [Theory]
    [InlineData("root", "root2/a/b.strm", false)]
    [InlineData("root", "root/a/b.strm", true)]
    [InlineData("root", "root", false)]
    [InlineData("root/", "root/a", true)]
    public void Writer_IsStrictlyInside_HandlesSiblingPrefixes(string root, string candidate, bool expected)
    {
        using var tmp = new TempDir();
        PlaceholderWriter.IsStrictlyInside(Path.GetFullPath(tmp.Sub(root)), Path.GetFullPath(tmp.Sub(candidate))).ShouldBe(expected);
    }

    [Fact]
    public void Writer_IsStrictlyInside_CaseVariantRoot_FollowsPlatform()
    {
        using var tmp = new TempDir();
        var root = Path.GetFullPath(tmp.Sub("Root"));
        var candidate = Path.GetFullPath(tmp.Sub("ROOT", "a.strm"));
        PlaceholderWriter.IsStrictlyInside(root, candidate).ShouldBe(OperatingSystem.IsWindows());
        PlaceholderWriter.Resolve(root, "../root2/a.strm").ShouldBeNull();
        PlaceholderWriter.Resolve(root, "../Root2/a/b.strm").ShouldBeNull();
    }

    [Fact]
    public async Task Writer_RefusesPathsThroughReparsePoints()
    {
        using var tmp = new TempDir();
        var root = tmp.Sub("root");
        var outside = tmp.Sub("outside");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, PlaceholderWriter.MarkerName), string.Empty);
        Directory.CreateDirectory(Path.Combine(outside, "Season 01"));
        File.WriteAllText(Path.Combine(outside, "Season 01", "victim.strm"), "keep");
        if (!TryLink(Path.Combine(root, "Linked"), outside))
        {
            return;
        }

        var result = await new PlaceholderWriter().ApplyAsync(
            Diff(
                [new FileWrite("Linked/Season 01/evil.strm", "pwn"), new FileWrite("Linked/x.nfo", "pwn")],
                ["Linked/Season 01/victim.strm"]),
            root,
            null,
            CancellationToken.None);

        result.Refused.ShouldBe(3);
        File.ReadAllText(Path.Combine(outside, "Season 01", "victim.strm")).ShouldBe("keep");
        File.Exists(Path.Combine(outside, "Season 01", "evil.strm")).ShouldBeFalse();
        File.Exists(Path.Combine(outside, "x.nfo")).ShouldBeFalse();
        PlaceholderWriter.ScanExisting(root).Keys.ShouldNotContain("Linked/Season 01/victim.strm");
    }

    private static bool TryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }
        }

        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        p.WaitForExit();
        return Directory.Exists(link);
    }

    [Fact]
    public async Task Writer_ScanExisting_HashesOnlyStrmAndNfo()
    {
        using var tmp = new TempDir();
        var root = tmp.Sub("root");
        await new PlaceholderWriter().ApplyAsync(
            Diff([Movie("strm", "url"), Movie("nfo", "<movie/>")]),
            root,
            (_, _, _) => Task.FromResult<Stream?>(new MemoryStream([9])),
            CancellationToken.None);

        var existing = PlaceholderWriter.ScanExisting(root);

        existing.Count.ShouldBe(2);
        existing[$"{MovieDir}/Sintel (2010).strm"].ShouldBe(PlaceholderPlanner.Hash("url"));
        existing[$"{MovieDir}/Sintel (2010).nfo"].ShouldBe(PlaceholderPlanner.Hash("<movie/>"));
        PlaceholderWriter.ScanExisting(tmp.Sub("missing")).ShouldBeEmpty();
    }
}
