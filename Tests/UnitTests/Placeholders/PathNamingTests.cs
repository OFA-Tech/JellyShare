using JellyShare.Application.Features.Placeholders;
using JellyShare.Domain.Keys;
using JellyShare.Domain.Models.Library;
using JellyShare.Domain.Models.Protocol;
using JellyShare.UnitTests.Fakes;

namespace JellyShare.UnitTests.Placeholders;

public sealed class PathNamingTests
{
    [Theory]
    [InlineData("Mission: Impossible", "Mission- Impossible")]
    [InlineData("What If...?", "What If")]
    [InlineData("AC/DC: Live", "AC-DC- Live")]
    [InlineData("  .  ", "Untitled")]
    [InlineData("a\tb\u0001c", "a b-c")]
    [InlineData("Two   Spaces", "Two Spaces")]
    [InlineData("<>:\"/\\|?*", "Untitled")]
    [InlineData("", "Untitled")]
    [InlineData("CON", "CON_")]
    [InlineData("nul", "nul_")]
    [InlineData("COM1", "COM1_")]
    [InlineData("Console", "Console")]
    public void Safe_ReplacesIllegalChars(string input, string expected) => PathNaming.Safe(input).ShouldBe(expected);

    [Fact]
    public void Safe_TruncatesTo120()
    {
        var result = PathNaming.Safe(new string('a', 300));
        result.Length.ShouldBe(120);
    }

    [Fact]
    public void Safe_Truncation_DoesNotSplitSurrogatePairOrLeaveTrailingSpace()
    {
        var input = new string('a', 119) + "\U0001F600" + "tail";
        var result = PathNaming.Safe(input);
        result.ShouldBe(new string('a', 119));

        var spaced = new string('a', 119) + " b";
        PathNaming.Safe(spaced).ShouldBe(new string('a', 119));
    }

    [Fact]
    public void Safe_IsDeterministic() => PathNaming.Safe("Foo: Bar?").ShouldBe(PathNaming.Safe("Foo: Bar?"));

    [Fact]
    public void Movie_Path_UsesTmdbFolderTag()
    {
        var item = TestItems.Movie("Sintel", tmdb: "45745", imdb: "tt1727587") with { Year = 2010 };
        var file = PathNaming.ForEntry(Entry(item));
        file.RelativeDir.ShouldBe("Sintel (2010) [tmdbid-45745]");
        file.BaseName.ShouldBe("Sintel (2010)");
        file.Kind.ShouldBe("Movie");
    }

    [Fact]
    public void Movie_Path_UsesImdbTagWhenNoTmdb_AndOmitsMissingYear()
    {
        var item = TestItems.Movie("Sintel", imdb: "tt1727587") with { Year = null };
        var file = PathNaming.ForEntry(Entry(item));
        file.RelativeDir.ShouldBe("Sintel [imdbid-tt1727587]");
        file.BaseName.ShouldBe("Sintel");
    }

    [Fact]
    public void Movie_Path_UsesFirstSourceForName()
    {
        var first = TestItems.Movie("Sintel", tmdb: "45745") with { Year = 2010 };
        var second = TestItems.Movie("Sintel (Remastered)", tmdb: "45745") with { Year = 2011 };
        var entry = new MergedEntry("movie:tmdb:45745", MediaKey.For(first), [(TestItems.PeerA, first), (TestItems.PeerB, second)]);
        PathNaming.ForEntry(entry).RelativeDir.ShouldBe("Sintel (2010) [tmdbid-45745]");
    }

    [Fact]
    public void Episode_Path_UsesSeasonFolder()
    {
        var item = TestItems.Episode("Pioneer One", new() { ["Tvdb"] = "153021" }, 2, 1);
        var file = PathNaming.ForEntry(Entry(item));
        file.RelativeDir.ShouldBe("Pioneer One [tvdbid-153021]/Season 02");
        file.BaseName.ShouldBe("Pioneer One S02E01");
        file.Kind.ShouldBe("Episode");
    }

    [Fact]
    public void Episode_Path_UsesTmdbWhenNoTvdb()
    {
        var item = TestItems.Episode("Pioneer One", new() { ["Tmdb"] = "77" }, 10, 12);
        var file = PathNaming.ForEntry(Entry(item));
        file.RelativeDir.ShouldBe("Pioneer One [tmdbid-77]/Season 10");
        file.BaseName.ShouldBe("Pioneer One S10E12");
    }

    [Fact]
    public void Episode_Path_WithLocalSeries_UsesLocalNameAndTvdbIdOnly()
    {
        var item = TestItems.Episode("Pioneer One (Remux)", new() { ["Tvdb"] = "153021", ["Tmdb"] = "77" }, 1, 3);
        var local = new LocalSeries("Pioneer One", new Dictionary<string, string> { ["Tvdb"] = "153021", ["Imdb"] = "tt1" });
        var file = PathNaming.ForEntry(Entry(item), local);
        file.RelativeDir.ShouldBe("Pioneer One [tvdbid-153021]/Season 01");
        file.BaseName.ShouldBe("Pioneer One S01E03");
    }

    [Fact]
    public void Episode_Path_WithLocalSeries_WithoutTvdb_UsesImdbOrNoTag()
    {
        var item = TestItems.Episode("Show X", new() { ["Tmdb"] = "77" }, 1, 1);
        var imdbLocal = new LocalSeries("Owner Show", new Dictionary<string, string> { ["Imdb"] = "tt9" });
        PathNaming.ForEntry(Entry(item), imdbLocal).RelativeDir.ShouldBe("Owner Show [imdbid-tt9]/Season 01");

        var nameOnly = new LocalSeries("Owner Show", new Dictionary<string, string> { ["Tmdb"] = "77" });
        PathNaming.ForEntry(Entry(item), nameOnly).RelativeDir.ShouldBe("Owner Show/Season 01");
    }

    [Fact]
    public void Episode_Path_LocalSeriesWithInvalidIds_AddsNoTagFromThem()
    {
        var item = TestItems.Episode("Show", new() { ["Tmdb"] = "77" }, 1, 1);
        var local = new LocalSeries("Owner", new Dictionary<string, string> { ["Tvdb"] = "1]/../../x", ["Imdb"] = "a:b" });
        PathNaming.ForEntry(Entry(item), local).RelativeDir.ShouldBe("Owner/Season 01");
    }

    [Fact]
    public void Movie_Path_IgnoresInvalidIdsInHandBuiltEntry()
    {
        var item = TestItems.Movie("Evil", tmdb: "1]/../../x") with { Year = null };
        var entry = new MergedEntry("movie:tmdb:1]/../../x", ["movie:tmdb:1]/../../x", "movie:imdb:a:b"], [(TestItems.PeerA, item)]);
        PathNaming.ForEntry(entry).RelativeDir.ShouldBe("Evil");
    }

    [Fact]
    public void PathNaming_ArtistWithSlash_IsSafe()
    {
        var file = PathNaming.ForEntry(Entry(TestItems.Track("Hells Bells", albumArtist: "AC/DC", album: "Back in Black", track: 1)));

        file.ShouldBe(new PlaceholderFile("AC-DC/Back in Black", "01 Hells Bells", "Audio"));
        PathNaming.IsSafeRelative(file.RelativeDir, file.BaseName).ShouldBeTrue();
    }

    [Fact]
    public void PathNaming_OwnerHasTheArtist_UsesOwnersSpelling()
    {
        var owner = OwnerArtists.Build(["Beyoncé", "AC/DC"]);

        PathNaming.ForEntry(Entry(TestItems.Track("Halo", albumArtist: "Beyonce", album: "I Am")), null, owner).RelativeDir.ShouldBe("Beyoncé/I Am");
        PathNaming.ForEntry(Entry(TestItems.Track("Song", albumArtist: "ACDC ", album: "X")), null, owner).RelativeDir.ShouldBe("ACDC/X");
        PathNaming.ForEntry(Entry(TestItems.Track("Song", albumArtist: "ac dc", album: "X")), null, owner).RelativeDir.ShouldBe("AC-DC/X");
        PathNaming.ForEntry(Entry(TestItems.Track("Song", albumArtist: "Other", album: "X")), null, owner).RelativeDir.ShouldBe("Other/X");
    }

    [Fact]
    public void PathNaming_BlankAlbumArtist_UsesFirstArtist_LikeTheAlbumKey()
    {
        var item = TestItems.Track("Song", albumArtist: "  ", album: "X") with { Artists = ["Solo"] };

        PathNaming.ForEntry(Entry(item)).RelativeDir.ShouldBe("Solo/X");
        TitleKey.For(Entry(item)).ShouldBe(TitleKey.For(Entry(item with { AlbumArtist = "Solo" })));
    }

    [Fact]
    public void PathNaming_Audio_SecondDiscPrefixesDisc_MissingNamesFallBack()
    {
        PathNaming.ForEntry(Entry(TestItems.Track("Song", disc: 2, track: 3))).BaseName.ShouldBe("2-03 Song");
        PathNaming.ForEntry(Entry(TestItems.Track("Song", track: null))).BaseName.ShouldBe("Song");

        var bare = TestItems.Track("Song", albumArtist: null, album: null) with { Artists = ["Solo"] };
        PathNaming.ForEntry(Entry(bare)).RelativeDir.ShouldBe("Solo/Unknown Album");
        PathNaming.ForEntry(Entry(bare with { Artists = null })).RelativeDir.ShouldBe("Unknown Artist/Unknown Album");
    }

    [Fact]
    public void PathNaming_Audiobook_KeepsFolderSegments()
    {
        var file = PathNaming.ForEntry(Entry(TestItems.Book("Chapter: One", folderPath: "Author X/Book Y") with { TrackNumber = 1 }));

        file.ShouldBe(new PlaceholderFile("Author X/Book Y", "01 Chapter- One", "AudioBook"));
        PathNaming.ForEntry(Entry(TestItems.Book("Chapter Two", folderPath: "Author X/Book Y"))).BaseName.ShouldBe("Chapter Two");
    }

    [Fact]
    public void PathNaming_Audiobook_UnsafeOrMissingFolder()
    {
        PathNaming.ForEntry(Entry(TestItems.Book("Part", folderPath: "../../etc/Book"))).RelativeDir.ShouldBe("Untitled/Untitled/etc/Book");
        PathNaming.ForEntry(Entry(TestItems.Book("Lone Book", folderPath: null))).RelativeDir.ShouldBe("Lone Book");
    }

    internal static MergedEntry Entry(CatalogItem item)
    {
        var keys = MediaKey.For(item);
        return new MergedEntry(keys[0], keys, [(TestItems.PeerA, item)]);
    }
}
