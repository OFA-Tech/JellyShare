using JellyShare.Domain.Keys;
using static JellyShare.UnitTests.Fakes.TestItems;

namespace JellyShare.UnitTests.Keys;

public sealed class MergeIndexTests
{
    private static Dictionary<string, string> Tvdb(string id) => new() { ["Tvdb"] = id };

    [Fact]
    public void Item_WithoutIds_IsUnmatched()
    {
        var index = MergeIndex.Build(new[] { (PeerA, Movie("Home Video")) });

        index.UnmatchedCount.ShouldBe(1);
        index.Entries.ShouldBeEmpty();
    }

    [Fact]
    public void SameMovie_FromTwoPeers_OneEntryTwoSources()
    {
        var index = MergeIndex.Build(new[]
        {
            (PeerA, Movie("Sintel", tmdb: "45745")),
            (PeerB, Movie("Sintel", tmdb: "45745")),
        });

        var entry = index.Entries.ShouldHaveSingleItem();
        entry.Sources.Count.ShouldBe(2);
        entry.Sources.Select(s => s.PeerId).ToArray().ShouldBe(new[] { PeerA, PeerB });
        entry.CanonicalKey.ShouldBe("movie:tmdb:45745");
        index.UnmatchedCount.ShouldBe(0);
    }

    [Fact]
    public void PeersSharingOnlyImdb_WithOneHavingTmdb_StillMerge()
    {
        var index = MergeIndex.Build(new[]
        {
            (PeerA, Movie("Sintel", tmdb: "45745", imdb: "tt1727587")),
            (PeerB, Movie("Sintel", imdb: "tt1727587")),
        });

        var entry = index.Entries.ShouldHaveSingleItem();
        entry.Sources.Count.ShouldBe(2);
        entry.CanonicalKey.ShouldBe("movie:tmdb:45745");
        entry.Keys.ShouldContain("movie:imdb:tt1727587");
        entry.Keys.ShouldContain("movie:tmdb:45745");
    }

    [Fact]
    public void ImdbOnly_CanonicalKeyIsImdb()
    {
        var index = MergeIndex.Build(new[] { (PeerA, Movie("Sintel", imdb: "tt1727587")) });

        index.Entries.ShouldHaveSingleItem().CanonicalKey.ShouldBe("movie:imdb:tt1727587");
    }

    [Fact]
    public void Episode_KeyedBySeriesIdSeasonEpisode()
    {
        var index = MergeIndex.Build(new[] { (PeerA, Episode("Show", Tvdb("153021"), 2, 1)) });

        var entry = index.Entries.ShouldHaveSingleItem();
        entry.CanonicalKey.ShouldBe("ep:tvdb:153021:s2e1");
        index.Find("ep:tvdb:153021:s2e1").ShouldBeSameAs(entry);
    }

    [Fact]
    public void Episode_PrefersTmdbCanonicalKey()
    {
        var seriesIds = new Dictionary<string, string> { ["Tvdb"] = "153021", ["Tmdb"] = "99" };
        var index = MergeIndex.Build(new[] { (PeerA, Episode("Show", seriesIds, 2, 1)) });

        index.Entries.ShouldHaveSingleItem().CanonicalKey.ShouldBe("ep:tmdb:99:s2e1");
    }

    [Fact]
    public void Episodes_SameSeriesDifferentEpisode_AreSeparate()
    {
        var index = MergeIndex.Build(new[]
        {
            (PeerA, Episode("Show", Tvdb("153021"), 1, 1)),
            (PeerA, Episode("Show", Tvdb("153021"), 1, 2)),
        });

        index.Entries.Count.ShouldBe(2);
    }

    [Fact]
    public void Episodes_SameEpisodeFromTwoPeers_Merge()
    {
        var index = MergeIndex.Build(new[]
        {
            (PeerA, Episode("Show", Tvdb("153021"), 1, 1)),
            (PeerB, Episode("Show", Tvdb("153021"), 1, 1)),
        });

        index.Entries.ShouldHaveSingleItem().Sources.Count.ShouldBe(2);
    }

    [Fact]
    public void SameName_DifferentIds_NotMerged()
    {
        var index = MergeIndex.Build(new[]
        {
            (PeerA, Movie("Dune", tmdb: "438631")),
            (PeerB, Movie("Dune", tmdb: "841")),
        });

        index.Entries.Count.ShouldBe(2);
        index.Entries.ShouldAllBe(e => e.Sources.Count == 1);
    }

    [Fact]
    public void Episode_WithoutSeasonOrEpisodeNumber_IsUnmatched()
    {
        var index = MergeIndex.Build(new[]
        {
            (PeerA, Episode("Show", Tvdb("153021"), null, 1)),
            (PeerA, Episode("Show", Tvdb("153021"), 1, null)),
        });

        index.UnmatchedCount.ShouldBe(2);
        index.Entries.ShouldBeEmpty();
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

        reversed.Entries.Select(e => e.CanonicalKey).ToArray().ShouldBe(forward.Entries.Select(e => e.CanonicalKey).ToArray());
        reversed.Entries.Select(e => string.Join('|', e.Keys)).ToArray().ShouldBe(forward.Entries.Select(e => string.Join('|', e.Keys)).ToArray());
        forward.Entries.Select(e => e.CanonicalKey).ToArray().ShouldBe(new[] { "movie:tmdb:438631", "movie:tmdb:45745", "movie:tmdb:7" });
    }

    [Fact]
    public void ConflictingTmdbIds_BridgedByImdb_AreUnmatched()
    {
        var index = MergeIndex.Build(new[]
        {
            (PeerA, Movie("X", tmdb: "9", imdb: "tt1")),
            (PeerB, Movie("X", tmdb: "10", imdb: "tt1")),
        });

        index.Entries.ShouldBeEmpty();
        index.Find("movie:tmdb:9").ShouldBeNull();
        index.Find("movie:tmdb:10").ShouldBeNull();
        index.Find("movie:imdb:tt1").ShouldBeNull();
        index.UnmatchedCount.ShouldBe(2);
        index.ConflictCount.ShouldBe(2);
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

        var entry = index.Entries.ShouldHaveSingleItem();
        entry.CanonicalKey.ShouldBe("movie:tmdb:5");
        index.UnmatchedCount.ShouldBe(2);
        index.ConflictCount.ShouldBe(2);
    }

    [Fact]
    public void ConflictingSeriesIds_ForEpisodes_AreUnmatched()
    {
        var index = MergeIndex.Build(new[]
        {
            (PeerA, Episode("Show", new Dictionary<string, string> { ["Tvdb"] = "1", ["Tmdb"] = "7" }, 1, 1)),
            (PeerB, Episode("Show", new Dictionary<string, string> { ["Tvdb"] = "2", ["Tmdb"] = "7" }, 1, 1)),
        });

        index.Entries.ShouldBeEmpty();
        index.UnmatchedCount.ShouldBe(2);
        index.ConflictCount.ShouldBe(2);
    }

    [Fact]
    public void SameTmdbFromTwoPeers_IsNotAConflict()
    {
        var index = MergeIndex.Build(new[]
        {
            (PeerA, Movie("X", tmdb: "9", imdb: "tt1")),
            (PeerB, Movie("X", tmdb: "9", imdb: "tt1")),
        });

        index.Entries.ShouldHaveSingleItem().Sources.Count.ShouldBe(2);
        index.ConflictCount.ShouldBe(0);
        index.UnmatchedCount.ShouldBe(0);
    }

    [Fact]
    public void Build_IsOrderIndependent_WithConflicts()
    {
        var a = (PeerA, Movie("X", tmdb: "9", imdb: "tt1"));
        var b = (PeerB, Movie("X", tmdb: "10", imdb: "tt1"));
        var c = (PeerC, Movie("Dune", tmdb: "438631"));

        var forward = MergeIndex.Build(new[] { a, b, c });
        var reversed = MergeIndex.Build(new[] { c, b, a });

        forward.Entries.ShouldHaveSingleItem().CanonicalKey.ShouldBe("movie:tmdb:438631");
        reversed.Entries.ShouldHaveSingleItem().CanonicalKey.ShouldBe("movie:tmdb:438631");
        reversed.ConflictCount.ShouldBe(forward.ConflictCount);
        reversed.UnmatchedCount.ShouldBe(forward.UnmatchedCount);
    }

    [Fact]
    public void Find_ReturnsEntryForAnyKey_AndNullForUnknown()
    {
        var index = MergeIndex.Build(new[] { (PeerA, Movie("Sintel", tmdb: "45745", imdb: "tt1727587")) });

        index.Find("movie:imdb:tt1727587").ShouldNotBeNull();
        index.Find("movie:tmdb:45745").ShouldBeSameAs(index.Find("movie:imdb:tt1727587"));
        index.Find("movie:tmdb:0").ShouldBeNull();
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

        var entry = index.Entries.ShouldHaveSingleItem();
        entry.Sources.Select(s => s.PeerId).ToArray().ShouldBe(new[] { PeerC, PeerA, PeerB });
    }
}
