using JellyShare.Domain.Keys;
using Xunit;
using static JellyShare.UnitTests.Fakes.TestItems;

namespace JellyShare.UnitTests.Keys;

public class MergeIndexTests
{
    private static Dictionary<string, string> Tvdb(string id) => new() { ["Tvdb"] = id };

    [Fact]
    public void Item_WithoutIds_IsUnmatched()
    {
        var index = MergeIndex.Build(new[] { (PeerA, Movie("Home Video")) });

        Assert.Equal(1, index.UnmatchedCount);
        Assert.Empty(index.Entries);
    }

    [Fact]
    public void SameMovie_FromTwoPeers_OneEntryTwoSources()
    {
        var index = MergeIndex.Build(new[]
        {
            (PeerA, Movie("Sintel", tmdb: "45745")),
            (PeerB, Movie("Sintel", tmdb: "45745")),
        });

        var entry = Assert.Single(index.Entries);
        Assert.Equal(2, entry.Sources.Count);
        Assert.Equal(new[] { PeerA, PeerB }, entry.Sources.Select(s => s.PeerId).ToArray());
        Assert.Equal("movie:tmdb:45745", entry.CanonicalKey);
        Assert.Equal(0, index.UnmatchedCount);
    }

    [Fact]
    public void PeersSharingOnlyImdb_WithOneHavingTmdb_StillMerge()
    {
        var index = MergeIndex.Build(new[]
        {
            (PeerA, Movie("Sintel", tmdb: "45745", imdb: "tt1727587")),
            (PeerB, Movie("Sintel", imdb: "tt1727587")),
        });

        var entry = Assert.Single(index.Entries);
        Assert.Equal(2, entry.Sources.Count);
        Assert.Equal("movie:tmdb:45745", entry.CanonicalKey);
        Assert.Contains("movie:imdb:tt1727587", entry.Keys);
        Assert.Contains("movie:tmdb:45745", entry.Keys);
    }

    [Fact]
    public void ImdbOnly_CanonicalKeyIsImdb()
    {
        var index = MergeIndex.Build(new[] { (PeerA, Movie("Sintel", imdb: "tt1727587")) });

        Assert.Equal("movie:imdb:tt1727587", Assert.Single(index.Entries).CanonicalKey);
    }

    [Fact]
    public void Episode_KeyedBySeriesIdSeasonEpisode()
    {
        var index = MergeIndex.Build(new[] { (PeerA, Episode("Show", Tvdb("153021"), 2, 1)) });

        var entry = Assert.Single(index.Entries);
        Assert.Equal("ep:tvdb:153021:s2e1", entry.CanonicalKey);
        Assert.Same(entry, index.Find("ep:tvdb:153021:s2e1"));
    }

    [Fact]
    public void Episode_PrefersTmdbCanonicalKey()
    {
        var seriesIds = new Dictionary<string, string> { ["Tvdb"] = "153021", ["Tmdb"] = "99" };
        var index = MergeIndex.Build(new[] { (PeerA, Episode("Show", seriesIds, 2, 1)) });

        Assert.Equal("ep:tmdb:99:s2e1", Assert.Single(index.Entries).CanonicalKey);
    }

    [Fact]
    public void Episodes_SameSeriesDifferentEpisode_AreSeparate()
    {
        var index = MergeIndex.Build(new[]
        {
            (PeerA, Episode("Show", Tvdb("153021"), 1, 1)),
            (PeerA, Episode("Show", Tvdb("153021"), 1, 2)),
        });

        Assert.Equal(2, index.Entries.Count);
    }

    [Fact]
    public void Episodes_SameEpisodeFromTwoPeers_Merge()
    {
        var index = MergeIndex.Build(new[]
        {
            (PeerA, Episode("Show", Tvdb("153021"), 1, 1)),
            (PeerB, Episode("Show", Tvdb("153021"), 1, 1)),
        });

        Assert.Equal(2, Assert.Single(index.Entries).Sources.Count);
    }

    [Fact]
    public void SameName_DifferentIds_NotMerged()
    {
        var index = MergeIndex.Build(new[]
        {
            (PeerA, Movie("Dune", tmdb: "438631")),
            (PeerB, Movie("Dune", tmdb: "841")),
        });

        Assert.Equal(2, index.Entries.Count);
        Assert.All(index.Entries, e => Assert.Single(e.Sources));
    }

    [Fact]
    public void Episode_WithoutSeasonOrEpisodeNumber_IsUnmatched()
    {
        var index = MergeIndex.Build(new[]
        {
            (PeerA, Episode("Show", Tvdb("153021"), null, 1)),
            (PeerA, Episode("Show", Tvdb("153021"), 1, null)),
        });

        Assert.Equal(2, index.UnmatchedCount);
        Assert.Empty(index.Entries);
    }

    [Fact]
    public void Build_IsOrderIndependent_ForEntriesKeysAndCanonicalKey()
    {
        var a = (PeerA, Movie("Sintel", tmdb: "45745", imdb: "tt1727587"));
        var b = (PeerB, Movie("Sintel", imdb: "tt1727587"));
        var c = (PeerC, Movie("Dune", tmdb: "438631"));
        var d = (PeerA, Movie("Other", tmdb: "7", imdb: "tt7"));

        var forward = MergeIndex.Build(new[] { a, b, c, d });
        var reversed = MergeIndex.Build(new[] { d, c, b, a });

        Assert.Equal(
            forward.Entries.Select(e => e.CanonicalKey).ToArray(),
            reversed.Entries.Select(e => e.CanonicalKey).ToArray());
        Assert.Equal(
            forward.Entries.Select(e => string.Join('|', e.Keys)).ToArray(),
            reversed.Entries.Select(e => string.Join('|', e.Keys)).ToArray());
        Assert.Equal(
            new[] { "movie:tmdb:438631", "movie:tmdb:45745", "movie:tmdb:7" },
            forward.Entries.Select(e => e.CanonicalKey).ToArray());
    }

    [Fact]
    public void ConflictingTmdbIds_BridgedByImdb_AreUnmatched()
    {
        var index = MergeIndex.Build(new[]
        {
            (PeerA, Movie("X", tmdb: "9", imdb: "tt1")),
            (PeerB, Movie("X", tmdb: "10", imdb: "tt1")),
        });

        Assert.Empty(index.Entries);
        Assert.Null(index.Find("movie:tmdb:9"));
        Assert.Null(index.Find("movie:tmdb:10"));
        Assert.Null(index.Find("movie:imdb:tt1"));
        Assert.Equal(2, index.UnmatchedCount);
        Assert.Equal(2, index.ConflictCount);
    }

    [Fact]
    public void ConflictingImdbIds_BridgedByTmdb_AreUnmatched()
    {
        var index = MergeIndex.Build(new[]
        {
            (PeerA, Movie("X", tmdb: "9", imdb: "tt1")),
            (PeerB, Movie("X", tmdb: "9", imdb: "tt2")),
            (PeerC, Movie("Y", tmdb: "5")),
        });

        var entry = Assert.Single(index.Entries);
        Assert.Equal("movie:tmdb:5", entry.CanonicalKey);
        Assert.Equal(2, index.UnmatchedCount);
        Assert.Equal(2, index.ConflictCount);
    }

    [Fact]
    public void ConflictingSeriesIds_ForEpisodes_AreUnmatched()
    {
        var index = MergeIndex.Build(new[]
        {
            (PeerA, Episode("Show", new Dictionary<string, string> { ["Tvdb"] = "1", ["Tmdb"] = "7" }, 1, 1)),
            (PeerB, Episode("Show", new Dictionary<string, string> { ["Tvdb"] = "2", ["Tmdb"] = "7" }, 1, 1)),
        });

        Assert.Empty(index.Entries);
        Assert.Equal(2, index.UnmatchedCount);
        Assert.Equal(2, index.ConflictCount);
    }

    [Fact]
    public void SameTmdbFromTwoPeers_IsNotAConflict()
    {
        var index = MergeIndex.Build(new[]
        {
            (PeerA, Movie("X", tmdb: "9", imdb: "tt1")),
            (PeerB, Movie("X", tmdb: "9", imdb: "tt1")),
        });

        Assert.Equal(2, Assert.Single(index.Entries).Sources.Count);
        Assert.Equal(0, index.ConflictCount);
        Assert.Equal(0, index.UnmatchedCount);
    }

    [Fact]
    public void Build_IsOrderIndependent_WithConflicts()
    {
        var a = (PeerA, Movie("X", tmdb: "9", imdb: "tt1"));
        var b = (PeerB, Movie("X", tmdb: "10", imdb: "tt1"));
        var c = (PeerC, Movie("Dune", tmdb: "438631"));

        var forward = MergeIndex.Build(new[] { a, b, c });
        var reversed = MergeIndex.Build(new[] { c, b, a });

        Assert.Equal("movie:tmdb:438631", Assert.Single(forward.Entries).CanonicalKey);
        Assert.Equal("movie:tmdb:438631", Assert.Single(reversed.Entries).CanonicalKey);
        Assert.Equal(forward.ConflictCount, reversed.ConflictCount);
        Assert.Equal(forward.UnmatchedCount, reversed.UnmatchedCount);
    }

    [Fact]
    public void Find_ReturnsEntryForAnyKey_AndNullForUnknown()
    {
        var index = MergeIndex.Build(new[] { (PeerA, Movie("Sintel", tmdb: "45745", imdb: "tt1727587")) });

        Assert.NotNull(index.Find("movie:imdb:tt1727587"));
        Assert.Same(index.Find("movie:imdb:tt1727587"), index.Find("movie:tmdb:45745"));
        Assert.Null(index.Find("movie:tmdb:0"));
    }

    [Fact]
    public void Sources_KeepInputOrder()
    {
        var index = MergeIndex.Build(new[]
        {
            (PeerC, Movie("S", tmdb: "1", id: "c")),
            (PeerA, Movie("S", tmdb: "1", id: "a")),
            (PeerB, Movie("S", tmdb: "1", id: "b")),
        });

        var entry = Assert.Single(index.Entries);
        Assert.Equal(new[] { PeerC, PeerA, PeerB }, entry.Sources.Select(s => s.PeerId).ToArray());
    }
}
