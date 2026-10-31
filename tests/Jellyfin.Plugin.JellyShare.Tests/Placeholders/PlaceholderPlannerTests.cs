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
