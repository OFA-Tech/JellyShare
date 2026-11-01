using System.Text;
using Jellyfin.Plugin.JellyShare.Placeholders;
using Jellyfin.Plugin.JellyShare.Tests.Consumer;
using Xunit;

namespace Jellyfin.Plugin.JellyShare.Tests.Placeholders;

public class PlaceholderWriterTests
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

        Assert.Equal("keep", File.ReadAllText(outside));
        Assert.Equal(9, result.Refused);
        Assert.Equal(1, result.Written);
        Assert.True(File.Exists(Path.Combine(root, MovieDir, "Sintel (2010).strm")));
        Assert.True(File.Exists(Path.Combine(root, PlaceholderWriter.MarkerName)));
        Assert.False(File.Exists(Path.Combine(root, "dir", "payload.exe")));
        Assert.All(result.ChangedDirs, d => Assert.StartsWith(Path.GetFullPath(root), d, StringComparison.Ordinal));
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
                Assert.Equal("new content", File.ReadAllText(tempPath));
                throw new IOException("simulated crash before commit");
            },
        };

        await Assert.ThrowsAsync<IOException>(
            () => writer.ApplyAsync(Diff([Movie("strm", "new content")]), root, null, CancellationToken.None));

        Assert.Equal(["old"], seenDuringWrite);
        Assert.Equal("old", File.ReadAllText(target));
        Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));

        await new PlaceholderWriter().ApplyAsync(Diff([Movie("strm", "new content")]), root, null, CancellationToken.None);
        Assert.Equal("new content", File.ReadAllText(target));
        Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));
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

        Assert.Equal(2, result.Deleted);
        Assert.False(Directory.Exists(Path.Combine(root, series, "Season 02")));
        Assert.True(File.Exists(Path.Combine(root, series, "Season 01", "Pioneer One S01E02.strm")));

        await new PlaceholderWriter().ApplyAsync(
            Diff(deletes: [$"{series}/Season 01/Pioneer One S01E02.strm", $"{series}/tvshow.nfo"]),
            root,
            null,
            CancellationToken.None);

        Assert.False(Directory.Exists(Path.Combine(root, series)));
        Assert.True(Directory.Exists(root));
        Assert.True(File.Exists(Path.Combine(root, PlaceholderWriter.MarkerName)));
    }

    [Fact]
    public async Task Writer_CreatesMarker_AndNeverDeletesIt()
    {
        using var tmp = new TempDir();
        var root = tmp.Sub("root");
        await new PlaceholderWriter().ApplyAsync(Diff([Movie("strm")]), root, null, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(root, PlaceholderWriter.MarkerName)));

        await new PlaceholderWriter().ApplyAsync(
            Diff(deletes: [$"{MovieDir}/Sintel (2010).strm", PlaceholderWriter.MarkerName]), root, null, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(root, PlaceholderWriter.MarkerName)));
        Assert.Single(Directory.GetFileSystemEntries(root));
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
        Assert.True(File.Exists(Path.Combine(root, MovieDir, "poster.jpg")));
        Assert.True(File.Exists(Path.Combine(root, MovieDir, "fanart.jpg")));

        var result = await new PlaceholderWriter().ApplyAsync(
            Diff(deletes: [$"{MovieDir}/Sintel (2010).strm", $"{MovieDir}/Sintel (2010).nfo"]), root, null, CancellationToken.None);

        Assert.False(Directory.Exists(Path.Combine(root, MovieDir)));
        Assert.Contains(Path.GetFullPath(Path.Combine(root, MovieDir)), result.ChangedDirs);
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

        Assert.Equal(3, result.Written);
        Assert.Equal(1, result.ImagesWritten);
        Assert.Equal(
            [(MovieDir, "Primary"), (MovieDir, "Backdrop"), (series, "Primary"), (series, "Backdrop")],
            calls.OrderBy(c => c.Dir == MovieDir ? 0 : 1).ThenBy(c => c.Type == "Primary" ? 0 : 1).ToList());
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(root, series, "poster.jpg")));
        Assert.False(File.Exists(Path.Combine(root, series, "fanart.jpg")));

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
        Assert.Equal([(series, "Backdrop")], calls);
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

        Assert.Equal(2, existing.Count);
        Assert.Equal(PlaceholderPlanner.Hash("url"), existing[$"{MovieDir}/Sintel (2010).strm"]);
        Assert.Equal(PlaceholderPlanner.Hash("<movie/>"), existing[$"{MovieDir}/Sintel (2010).nfo"]);
        Assert.Empty(PlaceholderWriter.ScanExisting(tmp.Sub("missing")));
    }
}
