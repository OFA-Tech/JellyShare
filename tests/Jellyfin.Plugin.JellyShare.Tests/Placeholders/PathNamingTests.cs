using Jellyfin.Plugin.JellyShare.Keys;
using Jellyfin.Plugin.JellyShare.Placeholders;
using Jellyfin.Plugin.JellyShare.Protocol;
using Jellyfin.Plugin.JellyShare.Tests.Keys;

using Xunit;

namespace Jellyfin.Plugin.JellyShare.Tests.Placeholders;

public class PathNamingTests
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
    public void Safe_ReplacesIllegalChars(string input, string expected) => Assert.Equal(expected, PathNaming.Safe(input));

    [Fact]
    public void Safe_TruncatesTo120()
    {
        var result = PathNaming.Safe(new string('a', 300));
        Assert.Equal(120, result.Length);
    }

    [Fact]
    public void Safe_Truncation_DoesNotSplitSurrogatePairOrLeaveTrailingSpace()
    {
        var input = new string('a', 119) + "\U0001F600" + "tail";
        var result = PathNaming.Safe(input);
        Assert.Equal(new string('a', 119), result);

        var spaced = new string('a', 119) + " b";
        Assert.Equal(new string('a', 119), PathNaming.Safe(spaced));
    }

    [Fact]
    public void Safe_IsDeterministic() => Assert.Equal(PathNaming.Safe("Foo: Bar?"), PathNaming.Safe("Foo: Bar?"));

    [Fact]
    public void Movie_Path_UsesTmdbFolderTag()
    {
        var item = TestItems.Movie("Sintel", tmdb: "45745", imdb: "tt1727587") with { Year = 2010 };
        var file = PathNaming.ForEntry(Entry(item));
        Assert.Equal("Sintel (2010) [tmdbid-45745]", file.RelativeDir);
        Assert.Equal("Sintel (2010)", file.BaseName);
        Assert.Equal("Movie", file.Kind);
    }

    [Fact]
    public void Movie_Path_UsesImdbTagWhenNoTmdb_AndOmitsMissingYear()
    {
        var item = TestItems.Movie("Sintel", imdb: "tt1727587") with { Year = null };
        var file = PathNaming.ForEntry(Entry(item));
        Assert.Equal("Sintel [imdbid-tt1727587]", file.RelativeDir);
        Assert.Equal("Sintel", file.BaseName);
    }

    [Fact]
    public void Movie_Path_UsesFirstSourceForName()
    {
        var first = TestItems.Movie("Sintel", tmdb: "45745") with { Year = 2010 };
        var second = TestItems.Movie("Sintel (Remastered)", tmdb: "45745") with { Year = 2011 };
        var entry = new MergedEntry("movie:tmdb:45745", MediaKey.For(first), [(TestItems.PeerA, first), (TestItems.PeerB, second)]);
        Assert.Equal("Sintel (2010) [tmdbid-45745]", PathNaming.ForEntry(entry).RelativeDir);
    }

    [Fact]
    public void Episode_Path_UsesSeasonFolder()
    {
        var item = TestItems.Episode("Pioneer One", new() { ["Tvdb"] = "153021" }, 2, 1);
        var file = PathNaming.ForEntry(Entry(item));
        Assert.Equal("Pioneer One [tvdbid-153021]/Season 02", file.RelativeDir);
        Assert.Equal("Pioneer One S02E01", file.BaseName);
        Assert.Equal("Episode", file.Kind);
    }

    [Fact]
    public void Episode_Path_UsesTmdbWhenNoTvdb()
    {
        var item = TestItems.Episode("Pioneer One", new() { ["Tmdb"] = "77" }, 10, 12);
        var file = PathNaming.ForEntry(Entry(item));
        Assert.Equal("Pioneer One [tmdbid-77]/Season 10", file.RelativeDir);
        Assert.Equal("Pioneer One S10E12", file.BaseName);
    }

    [Fact]
    public void Episode_Path_WithLocalSeries_UsesLocalNameAndTvdbIdOnly()
    {
        var item = TestItems.Episode("Pioneer One (Remux)", new() { ["Tvdb"] = "153021", ["Tmdb"] = "77" }, 1, 3);
        var local = new LocalSeries("Pioneer One", new Dictionary<string, string> { ["Tvdb"] = "153021", ["Imdb"] = "tt1" });
        var file = PathNaming.ForEntry(Entry(item), local);
        Assert.Equal("Pioneer One [tvdbid-153021]/Season 01", file.RelativeDir);
        Assert.Equal("Pioneer One S01E03", file.BaseName);
    }

    [Fact]
    public void Episode_Path_WithLocalSeries_WithoutTvdb_UsesImdbOrNoTag()
    {
        var item = TestItems.Episode("Show X", new() { ["Tmdb"] = "77" }, 1, 1);
        var imdbLocal = new LocalSeries("Owner Show", new Dictionary<string, string> { ["Imdb"] = "tt9" });
        Assert.Equal("Owner Show [imdbid-tt9]/Season 01", PathNaming.ForEntry(Entry(item), imdbLocal).RelativeDir);

        var nameOnly = new LocalSeries("Owner Show", new Dictionary<string, string> { ["Tmdb"] = "77" });
        Assert.Equal("Owner Show/Season 01", PathNaming.ForEntry(Entry(item), nameOnly).RelativeDir);
    }

    [Fact]
    public void Episode_Path_LocalSeriesWithInvalidIds_AddsNoTagFromThem()
    {
        var item = TestItems.Episode("Show", new() { ["Tmdb"] = "77" }, 1, 1);
        var local = new LocalSeries("Owner", new Dictionary<string, string> { ["Tvdb"] = "1]/../../x", ["Imdb"] = "a:b" });
        Assert.Equal("Owner/Season 01", PathNaming.ForEntry(Entry(item), local).RelativeDir);
    }

    [Fact]
    public void Movie_Path_IgnoresInvalidIdsInHandBuiltEntry()
    {
        var item = TestItems.Movie("Evil", tmdb: "1]/../../x") with { Year = null };
        var entry = new MergedEntry("movie:tmdb:1]/../../x", ["movie:tmdb:1]/../../x", "movie:imdb:a:b"], [(TestItems.PeerA, item)]);
        Assert.Equal("Evil", PathNaming.ForEntry(entry).RelativeDir);
    }

    internal static MergedEntry Entry(CatalogItem item)
    {
        var keys = MediaKey.For(item);
        return new MergedEntry(keys[0], keys, [(TestItems.PeerA, item)]);
    }
}
