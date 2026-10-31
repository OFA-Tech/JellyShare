using System.Diagnostics;
using Jellyfin.Plugin.JellyShare.Keys;
using Jellyfin.Plugin.JellyShare.Placeholders;
using Jellyfin.Plugin.JellyShare.Protocol;
using Jellyfin.Plugin.JellyShare.Tests.Keys;

using Xunit;

namespace Jellyfin.Plugin.JellyShare.Tests.Placeholders;

public class PlaceholderPlannerTests
{
    private const string Relay = "http://127.0.0.1:8096/jellyfin";
    private static readonly HashSet<string> NoLocal = [];
    private static readonly Dictionary<string, string> NoExisting = [];

    private static CatalogItem Sintel() =>
        TestItems.Movie("Sintel", tmdb: "45745", imdb: "tt1727587") with { Year = 2010 };

    private static CatalogItem Pioneer(int season, int episode, string id) =>
        TestItems.Episode("Pioneer One", new() { ["Tvdb"] = "153021" }, season, episode, id);

    private static MergedEntry Entry(CatalogItem item) => PathNamingTests.Entry(item);

    private static Dictionary<string, string> Hashed(PlaceholderDiff diff)
        => diff.Writes.ToDictionary(w => w.RelativePath, w => PlaceholderPlanner.Hash(w.Content));

    [Fact]
    public void Plan_WritesStrmAndNfoForMovie()
    {
        var diff = PlaceholderPlanner.Plan([Entry(Sintel())], NoLocal, NoExisting, Relay);
        var paths = diff.Writes.Select(w => w.RelativePath).ToList();
        Assert.Equal(
            ["Sintel (2010) [tmdbid-45745]/Sintel (2010).nfo", "Sintel (2010) [tmdbid-45745]/Sintel (2010).strm"],
            paths);
        Assert.Equal(
            "http://127.0.0.1:8096/jellyfin/JellyShare/v1/play/movie%3Atmdb%3A45745",
            diff.Writes.Single(w => w.RelativePath.EndsWith(".strm", StringComparison.Ordinal)).Content);
        Assert.Empty(diff.Deletes);
        Assert.Equal(["Sintel (2010) [tmdbid-45745]"], diff.TouchedDirs);
    }

    [Fact]
    public void Plan_SkipsEntriesPresentLocally()
    {
        var diff = PlaceholderPlanner.Plan(
            [Entry(Sintel())], new HashSet<string> { "movie:imdb:tt1727587" }, NoExisting, Relay);
        Assert.Empty(diff.Writes);
        Assert.Empty(diff.Deletes);
        Assert.Empty(diff.TouchedDirs);
    }

    [Fact]
    public void Plan_Episodes_WriteTvShowNfoOncePerSeriesDir()
    {
        var wanted = new[] { Entry(Pioneer(1, 1, "a")), Entry(Pioneer(1, 2, "b")), Entry(Pioneer(2, 1, "c")) };
        var diff = PlaceholderPlanner.Plan(wanted, NoLocal, NoExisting, Relay);
        var paths = diff.Writes.Select(w => w.RelativePath).ToList();
        Assert.Equal(7, paths.Count);
        Assert.Single(paths, p => p == "Pioneer One [tvdbid-153021]/tvshow.nfo");
        Assert.Contains("Pioneer One [tvdbid-153021]/Season 02/Pioneer One S02E01.strm", paths);
        Assert.Contains("Pioneer One [tvdbid-153021]/Season 02/Pioneer One S02E01.nfo", paths);
        Assert.Equal(
            ["Pioneer One [tvdbid-153021]", "Pioneer One [tvdbid-153021]/Season 01", "Pioneer One [tvdbid-153021]/Season 02"],
            diff.TouchedDirs);
    }

    [Fact]
    public void Plan_UsesLocalSeriesNameAndIds()
    {
        var item = TestItems.Episode("Remote Name", new() { ["Tvdb"] = "153021", ["Tmdb"] = "77" }, 1, 1);
        var local = new Dictionary<string, LocalSeries>
        {
            ["tvdb:153021"] = new("Owner Name", new Dictionary<string, string> { ["Tvdb"] = "153021", ["Imdb"] = "tt1" }),
        };
        var diff = PlaceholderPlanner.Plan([Entry(item)], NoLocal, NoExisting, Relay, local);

        Assert.All(diff.Writes, w => Assert.StartsWith("Owner Name [tvdbid-153021]/", w.RelativePath, StringComparison.Ordinal));
        var show = diff.Writes.Single(w => w.RelativePath == "Owner Name [tvdbid-153021]/tvshow.nfo");
        Assert.Contains("<title>Owner Name</title>", show.Content, StringComparison.Ordinal);
        Assert.Contains("type=\"imdb\"", show.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("tmdb", show.Content, StringComparison.Ordinal);
        Assert.Contains(diff.Writes, w => w.RelativePath == "Owner Name [tvdbid-153021]/Season 01/Owner Name S01E01.strm");
    }

    [Fact]
    public void Plan_LocalSeriesLookup_WorksByAnySeriesId()
    {
        var item = TestItems.Episode("Remote", new() { ["Tmdb"] = "77" }, 1, 1);
        var local = new Dictionary<string, LocalSeries>
        {
            ["tmdb:77"] = new("Owner", new Dictionary<string, string> { ["Tmdb"] = "77" }),
        };
        var diff = PlaceholderPlanner.Plan([Entry(item)], NoLocal, NoExisting, Relay, local);
        Assert.Contains(diff.Writes, w => w.RelativePath == "Owner/tvshow.nfo");
    }

    [Fact]
    public void Plan_SecondRunWithSameInput_WritesNothing()
    {
        var wanted = new[] { Entry(Sintel()), Entry(Pioneer(1, 1, "a")), Entry(Pioneer(1, 2, "b")) };
        var first = PlaceholderPlanner.Plan(wanted, NoLocal, NoExisting, Relay);
        Assert.NotEmpty(first.Writes);

        var second = PlaceholderPlanner.Plan(wanted, NoLocal, Hashed(first), Relay);
        Assert.Empty(second.Writes);
        Assert.Empty(second.Deletes);
        Assert.Empty(second.TouchedDirs);
    }

    [Fact]
    public void Plan_RewritesFilesWhoseHashDiffers()
    {
        var first = PlaceholderPlanner.Plan([Entry(Sintel())], NoLocal, NoExisting, Relay);
        var existing = Hashed(first);
        var nfo = first.Writes.Single(w => w.RelativePath.EndsWith(".nfo", StringComparison.Ordinal)).RelativePath;
        existing[nfo] = "stale";

        var second = PlaceholderPlanner.Plan([Entry(Sintel())], NoLocal, existing, Relay);
        Assert.Equal([nfo], second.Writes.Select(w => w.RelativePath));
    }

    [Fact]
    public void Plan_DeletesPlaceholdersNoLongerOffered()
    {
        var wanted = new[] { Entry(Sintel()), Entry(Pioneer(1, 1, "a")), Entry(Pioneer(1, 2, "b")) };
        var existing = Hashed(PlaceholderPlanner.Plan(wanted, NoLocal, NoExisting, Relay));

        var diff = PlaceholderPlanner.Plan([Entry(Pioneer(1, 1, "a")), Entry(Pioneer(1, 2, "b"))], NoLocal, existing, Relay);
        Assert.Empty(diff.Writes);
        Assert.Equal(
            ["Sintel (2010) [tmdbid-45745]/Sintel (2010).nfo", "Sintel (2010) [tmdbid-45745]/Sintel (2010).strm"],
            diff.Deletes);
        Assert.Equal(["Sintel (2010) [tmdbid-45745]"], diff.TouchedDirs);
    }

    [Fact]
    public void Plan_DeletesTvShowNfoOnlyWhenNoEpisodeRemains()
    {
        var wanted = new[] { Entry(Pioneer(1, 1, "a")), Entry(Pioneer(1, 2, "b")) };
        var existing = Hashed(PlaceholderPlanner.Plan(wanted, NoLocal, NoExisting, Relay));
        const string tvshow = "Pioneer One [tvdbid-153021]/tvshow.nfo";

        var partial = PlaceholderPlanner.Plan([Entry(Pioneer(1, 1, "a"))], NoLocal, existing, Relay);
        Assert.DoesNotContain(tvshow, partial.Deletes);
        Assert.Equal(2, partial.Deletes.Count);

        var none = PlaceholderPlanner.Plan([], NoLocal, existing, Relay);
        Assert.Contains(tvshow, none.Deletes);
        Assert.Equal(5, none.Deletes.Count);
    }

    [Fact]
    public void Plan_NeverDeletesMarkerOrImages()
    {
        var existing = new Dictionary<string, string>
        {
            [".jellyshare"] = "x",
            ["Sintel (2010) [tmdbid-45745]/poster.jpg"] = "x",
            ["Sintel (2010) [tmdbid-45745]/folder.png"] = "x",
            ["Pioneer One [tvdbid-153021]/Season 01/Pioneer One S01E01-thumb.jpg"] = "x",
        };
        var diff = PlaceholderPlanner.Plan([], NoLocal, existing, Relay);
        Assert.Empty(diff.Deletes);
        Assert.Empty(diff.TouchedDirs);
    }

    [Fact]
    public void Plan_LocalKeyAppearing_DeletesExistingPlaceholder()
    {
        var existing = Hashed(PlaceholderPlanner.Plan([Entry(Sintel())], NoLocal, NoExisting, Relay));
        var diff = PlaceholderPlanner.Plan(
            [Entry(Sintel())], new HashSet<string> { "movie:tmdb:45745" }, existing, Relay);
        Assert.Equal(2, diff.Deletes.Count);
        Assert.Empty(diff.Writes);
    }

    private static void AssertPathsStayInsideRoot(PlaceholderDiff diff)
    {
        foreach (var path in diff.Writes.Select(w => w.RelativePath).Concat(diff.Deletes).Concat(diff.TouchedDirs))
        {
            Assert.False(Path.IsPathRooted(path), path);
            Assert.DoesNotContain('\\', path);
            Assert.DoesNotContain(':', path);
            foreach (var segment in path.Split('/'))
            {
                Assert.NotEqual("..", segment);
                Assert.NotEqual(".", segment);
                Assert.NotEqual(string.Empty, segment);
            }
        }
    }

    [Fact]
    public void Plan_MaliciousProviderId_NeverEscapesRoot()
    {
        const string evil = "1]/../../x";
        var movie = TestItems.Movie("Evil", tmdb: evil, imdb: "tt1");
        var movieNoValid = TestItems.Movie("Evil2", tmdb: evil);
        var episode = TestItems.Episode("Evil Show", new() { ["Tvdb"] = "..\\..\\x", ["Tmdb"] = "9" }, 1, 1);

        // hand-built entries bypass MediaKey, as a buggy or hostile caller could
        var handBuiltMovie = new MergedEntry(
            "movie:tmdb:" + evil, ["movie:tmdb:" + evil, "movie:tmdb:a:b"], [(TestItems.PeerA, movie with { ProviderIds = new() { ["Tmdb"] = evil } })]);
        var handBuiltEpisode = new MergedEntry(
            "ep:tvdb:../..:s1e1", ["ep:tvdb:../..:s1e1"], [(TestItems.PeerA, episode with { SeriesProviderIds = new() { ["Tvdb"] = "../../x", ["Imdb"] = "a/b" } })]);

        var wanted = new[] { Entry(movie), handBuiltMovie, handBuiltEpisode, Entry(episode) };
        var diff = PlaceholderPlanner.Plan(wanted, NoLocal, NoExisting, Relay);

        AssertPathsStayInsideRoot(diff);
        Assert.Contains(diff.Writes, w => w.RelativePath == "Evil (2020) [imdbid-tt1]/Evil (2020).strm");
        Assert.Contains(diff.Writes, w => w.RelativePath.StartsWith("Evil Show [tmdbid-9]/", StringComparison.Ordinal));
        Assert.DoesNotContain(diff.Writes, w => w.RelativePath.Contains("..", StringComparison.Ordinal));
        Assert.Equal(0, diff.Skipped);
        Assert.Empty(MediaKey.For(movieNoValid));

        var second = PlaceholderPlanner.Plan(wanted, NoLocal, Hashed(diff), Relay);
        Assert.Empty(second.Writes);
        AssertPathsStayInsideRoot(second);
    }

    [Theory]
    [InlineData("a/../b", "x", false)]
    [InlineData("../a", "x", false)]
    [InlineData("a/./b", "x", false)]
    [InlineData("/abs", "x", false)]
    [InlineData("a//b", "x", false)]
    [InlineData("a/", "x", false)]
    [InlineData("", "x", false)]
    [InlineData("C:/x", "x", false)]
    [InlineData("a\\b", "x", false)]
    [InlineData("ok", "..", false)]
    [InlineData("ok", "a/b", false)]
    [InlineData("ok", "", false)]
    [InlineData("Show [tvdbid-1]/Season 01", "Show S01E01", true)]
    [InlineData("Sintel (2010) [tmdbid-45745]", "Sintel (2010)", true)]
    public void IsSafeRelative_RejectsRootedOrTraversingPaths(string dir, string baseName, bool expected)
        => Assert.Equal(expected, PathNaming.IsSafeRelative(dir, baseName));

    [Fact]
    public void Plan_NamesLikeDotDot_AreSanitizedAndNotSkipped()
    {
        var item = TestItems.Movie("..", tmdb: "1");
        var diff = PlaceholderPlanner.Plan([Entry(item)], NoLocal, NoExisting, Relay);
        AssertPathsStayInsideRoot(diff);
        Assert.Equal(0, diff.Skipped);
        Assert.Contains(diff.Writes, w => w.RelativePath == "Untitled (2020) [tmdbid-1]/Untitled (2020).strm");
    }

    [Fact]
    public void Plan_SeriesNfo_UsesIdsMergedAcrossSources_ConsistentWithFolderTag()
    {
        var a = TestItems.Episode("Pioneer One", new() { ["Tvdb"] = "153021" }, 1, 1, "a");
        var b = TestItems.Episode("Pioneer One", new() { ["Tvdb"] = "153021", ["Imdb"] = "tt1759761", ["Tmdb"] = "77" }, 1, 1, "b");
        var entry = new MergedEntry(
            "ep:tmdb:77:s1e1", ["ep:tmdb:77:s1e1", "ep:tvdb:153021:s1e1"], [(TestItems.PeerA, a), (TestItems.PeerB, b)]);

        var diff = PlaceholderPlanner.Plan([entry], NoLocal, NoExisting, Relay);
        var show = diff.Writes.Single(w => w.RelativePath == "Pioneer One [tvdbid-153021]/tvshow.nfo");
        var ids = System.Xml.Linq.XDocument.Parse(show.Content).Root!.Elements("uniqueid")
            .ToDictionary(e => (string)e.Attribute("type")!, e => e.Value);
        Assert.Equal("153021", ids["tvdb"]);
        Assert.Equal("tt1759761", ids["imdb"]);
        Assert.Equal("77", ids["tmdb"]);
    }

    [Fact]
    public void Plan_Output_IsIndependentOfInputOrder()
    {
        var a = new[] { Entry(Sintel()), Entry(Pioneer(1, 1, "a")), Entry(Pioneer(1, 2, "b")) };
        var b = a.Reverse().ToArray();
        var da = PlaceholderPlanner.Plan(a, NoLocal, NoExisting, Relay);
        var db = PlaceholderPlanner.Plan(b, NoLocal, NoExisting, Relay);
        Assert.Equal(da.Writes.Select(w => (w.RelativePath, w.Content)), db.Writes.Select(w => (w.RelativePath, w.Content)));
    }

    [Fact]
    public void Plan_10000Entries_Under2Seconds()
    {
        var items = new List<(Guid, CatalogItem)>();
        for (var i = 0; i < 6000; i++)
        {
            items.Add((TestItems.PeerA, TestItems.Movie($"Movie {i}", tmdb: i.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        }

        for (var i = 0; i < 4000; i++)
        {
            var series = (i / 20).ToString(System.Globalization.CultureInfo.InvariantCulture);
            items.Add((TestItems.PeerA, TestItems.Episode($"Show {series}", new() { ["Tvdb"] = series }, 1 + (i % 20 / 10), 1 + (i % 10), "e" + i)));
        }

        var index = MergeIndex.Build(items);
        Assert.Equal(10_000, index.Entries.Count);

        var sw = Stopwatch.StartNew();
        var diff = PlaceholderPlanner.Plan(index.Entries, NoLocal, NoExisting, Relay);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"took {sw.Elapsed}");
        Assert.Equal((10_000 * 2) + 200, diff.Writes.Count);

        sw.Restart();
        var again = PlaceholderPlanner.Plan(index.Entries, NoLocal, Hashed(diff), Relay);
        sw.Stop();
        Assert.Empty(again.Writes);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"second run took {sw.Elapsed}");
    }
}
