using Jellyfin.Plugin.JellyShare.Consumer;
using Xunit;

namespace Jellyfin.Plugin.JellyShare.Tests.Consumer;

public class LocalIndexTests
{
    private static readonly Dictionary<string, string> PioneerIds = new() { ["Tvdb"] = "153021" };

    private static (LocalIndex Index, FakeLocalLibrary Library, ConsumerConfig Config) Create(
        Func<string, bool> exists, TimeSpan? budget = null)
    {
        var library = new FakeLocalLibrary();
        var config = new ConsumerConfig();
        config.Current.MoviesPlaceholderPath = "/ph/Movies";
        config.Current.ShowsPlaceholderPath = "/ph/Shows";
        return (new LocalIndex(library, config, FakeLocalLibrary.Exists(exists), budget), library, config);
    }

    private static Dictionary<string, string> Tmdb(string id) => new() { ["Tmdb"] = id };

    private static (LocalIndex Index, FakeLocalLibrary Library) Real(Func<string, FilePresence>? probe = null)
    {
        var library = new FakeLocalLibrary();
        var config = new ConsumerConfig();
        config.Current.MoviesPlaceholderPath = "/ph/Movies";
        config.Current.ShowsPlaceholderPath = "/ph/Shows";
        return (new LocalIndex(library, config, probe), library);
    }


    [Fact]
    public async Task LocalIndex_RealProbe_MissingFileIsMissing_ExistingIsLocal()
    {
        using var tmp = new TempDir();
        var root = tmp.Sub("Movies");
        Directory.CreateDirectory(Path.Combine(root, "A"));
        File.WriteAllText(Path.Combine(root, "A", "a.mkv"), "x");
        var (index, library) = Real();
        library.Media.Add(new LocalMedia("Movie", Path.Combine(root, "A", "a.mkv"), Tmdb("1"), null, null, null));
        library.Media.Add(new LocalMedia("Movie", Path.Combine(root, "B", "b.mkv"), Tmdb("2"), null, null, null));

        var result = await index.BuildAsync(CancellationToken.None);

        Assert.Equal(["movie:tmdb:1"], result.MovieKeys);
        Assert.Equal(1, result.MissingFiles);
        Assert.Equal(0, result.UncheckedFiles);
    }

    [Fact]
    public async Task LocalIndex_MissingRoot_ItemsAreMissing()
    {
        using var tmp = new TempDir();
        var root = tmp.Sub("offline-drive");
        var (index, library) = Real();
        library.Media.Add(new LocalMedia("Movie", Path.Combine(root, "A", "a.mkv"), Tmdb("1"), null, null, null));

        var result = await index.BuildAsync(CancellationToken.None);

        Assert.Empty(result.MovieKeys);
        Assert.Equal(1, result.MissingFiles);
        Assert.Equal(0, result.UncheckedFiles);
    }

    [Fact]
    public async Task LocalIndex_EmptyMountPoint_ItemsAreMissing()
    {
        using var tmp = new TempDir();
        var root = tmp.Sub("mountpoint");
        Directory.CreateDirectory(root);
        var (index, library) = Real();
        library.Media.Add(new LocalMedia("Movie", Path.Combine(root, "A", "a.mkv"), Tmdb("1"), null, null, null));
        library.Media.Add(new LocalMedia("Episode", Path.Combine(root, "S", "e.mkv"), null, new Dictionary<string, string> { ["Tvdb"] = "9" }, 1, 2));

        var result = await index.BuildAsync(CancellationToken.None);

        Assert.Empty(result.MovieKeys);
        Assert.Empty(result.EpisodeKeys);
        Assert.Equal(2, result.MissingFiles);
    }

    [Fact]
    public async Task LocalIndex_IoErrorOtherThanNotFound_IsPresent()
    {
        var (index, library) = Real(p => p.Contains("throws", StringComparison.Ordinal)
            ? throw new IOException("stale file handle")
            : FilePresence.Unknown);
        library.Media.Add(new LocalMedia("Movie", "/media/denied.mkv", Tmdb("1"), null, null, null));
        library.Media.Add(new LocalMedia("Movie", "/media/throws.mkv", Tmdb("2"), null, null, null));

        var result = await index.BuildAsync(CancellationToken.None);

        Assert.Equal(["movie:tmdb:1", "movie:tmdb:2"], result.MovieKeys.Order(StringComparer.Ordinal));
        Assert.Equal(0, result.MissingFiles);
        Assert.Equal(2, result.UncheckedFiles);
    }

    [Fact]
    public void LocalIndex_DefaultProbe_DistinguishesNotFoundFromErrors()
    {
        using var tmp = new TempDir();
        var file = tmp.Sub("a.mkv");
        File.WriteAllText(file, "x");

        Assert.Equal(FilePresence.Present, LocalIndex.ProbeFile(file));
        Assert.Equal(FilePresence.Missing, LocalIndex.ProbeFile(tmp.Sub("b.mkv")));
        Assert.Equal(FilePresence.Missing, LocalIndex.ProbeFile(tmp.Sub("nodir", "b.mkv")));
        Assert.Equal(FilePresence.Unknown, LocalIndex.ProbeFile("bad\0path.mkv"));
    }

    private static LocalMedia Sintel(string path) => new("Movie", path, new Dictionary<string, string> { ["Tmdb"] = "45745" }, null, null, null);

    [Fact]
    public async Task LocalIndex_ExistingFile_IsLocal()
    {
        var (index, library, _) = Create(_ => true);
        library.Media.Add(Sintel("/media/Movies/Sintel.mkv"));
        library.Media.Add(new LocalMedia("Episode", "/media/Shows/p/s01e01.mkv", null, PioneerIds, 1, 1));

        var result = await index.BuildAsync(CancellationToken.None);

        Assert.Equal(["movie:tmdb:45745"], result.MovieKeys);
        Assert.Equal(["ep:tvdb:153021:s1e1"], result.EpisodeKeys);
        Assert.Equal(["movie:tmdb:45745"], index.GetLocalKeys("Movie"));
        Assert.Equal(0, result.UncheckedFiles);
    }

    [Fact]
    public async Task LocalIndex_MissingFile_NotLocal()
    {
        var (index, library, _) = Create(p => !p.Contains("Sintel", StringComparison.Ordinal));
        library.Media.Add(Sintel("/media/Movies/Sintel.mkv"));
        var result = await index.BuildAsync(CancellationToken.None);
        Assert.Empty(result.MovieKeys);
        Assert.Equal(1, result.MissingFiles);
    }

    [Fact]
    public async Task LocalIndex_ExcludesPlaceholdersAndStrm()
    {
        var checkedPaths = new List<string>();
        var (index, library, _) = Create(p =>
        {
            checkedPaths.Add(p);
            return true;
        });
        library.Media.Add(Sintel("/ph/Movies/Sintel (2010) [tmdbid-45745]/Sintel (2010).mkv"));
        library.Media.Add(Sintel("/elsewhere/Sintel.strm"));
        library.Media.Add(Sintel(""));
        library.Media.Add(new LocalMedia("Episode", "/ph/Shows/Pioneer One/Season 01/x.strm", null, PioneerIds, 1, 1));

        var result = await index.BuildAsync(CancellationToken.None);

        Assert.Empty(result.MovieKeys);
        Assert.Empty(result.EpisodeKeys);
        Assert.Empty(checkedPaths);
    }

    [Fact]
    public async Task LocalIndex_ReturnsLocalSeries_ExcludingPlaceholderSeries()
    {
        var (index, library, _) = Create(_ => true);
        library.Series.Add(new LocalSeriesItem("Pioneer One", "/media/Shows/Pioneer One [tvdbid-153021]",
            new Dictionary<string, string> { ["Tvdb"] = "153021", ["Imdb"] = "TT1748166" }));
        library.Series.Add(new LocalSeriesItem("Other", "/ph/Shows/Other [tvdbid-1]", new Dictionary<string, string> { ["Tvdb"] = "1" }));

        var result = await index.BuildAsync(CancellationToken.None);

        Assert.Equal(["imdb:tt1748166", "tvdb:153021"], result.Series.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("Pioneer One", result.Series["tvdb:153021"].Name);
    }

    [Fact]
    public async Task LocalIndex_ExistenceBudgetExceeded_TreatsUncheckedAsPresent()
    {
        using var hang = new ManualResetEventSlim(false);
        var (index, library, _) = Create(
            p =>
            {
                if (p.StartsWith("/nfs/", StringComparison.Ordinal))
                {
                    hang.Wait(TimeSpan.FromSeconds(10));
                }

                return false;
            },
            TimeSpan.FromMilliseconds(200));
        library.Media.Add(new LocalMedia("Movie", "/media/gone.mkv", new Dictionary<string, string> { ["Tmdb"] = "1" }, null, null, null));
        library.Media.Add(new LocalMedia("Movie", "/nfs/a.mkv", new Dictionary<string, string> { ["Tmdb"] = "2" }, null, null, null));
        library.Media.Add(new LocalMedia("Movie", "/nfs/b.mkv", new Dictionary<string, string> { ["Tmdb"] = "3" }, null, null, null));

        var started = DateTime.UtcNow;
        var result = await index.BuildAsync(CancellationToken.None);
        hang.Set();

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.Equal(["movie:tmdb:2", "movie:tmdb:3"], result.MovieKeys.Order(StringComparer.Ordinal));
        Assert.Equal(2, result.UncheckedFiles);
        Assert.Equal(1, result.MissingFiles);
    }
}
