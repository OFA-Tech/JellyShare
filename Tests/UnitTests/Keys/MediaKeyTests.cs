using JellyShare.Domain.Keys;
using static JellyShare.UnitTests.Fakes.TestItems;

namespace JellyShare.UnitTests.Keys;

public sealed class MediaKeyTests
{
    [Fact]
    public void Movie_WithTmdbAndImdb_HasBothKeys()
    {
        var keys = MediaKey.For(Movie("Sintel", tmdb: "45745", imdb: "tt1727587"));

        keys.OrderBy(k => k, StringComparer.Ordinal).ToArray().ShouldBe(new[] { "movie:imdb:tt1727587", "movie:tmdb:45745" });
    }

    [Fact]
    public void Movie_ProviderNamesAreCaseInsensitive_IdsLowercased()
    {
        var item = Movie("X") with
        {
            ProviderIds = new Dictionary<string, string> { ["TMDB"] = " 45745 ", ["imdb"] = "TT1727587" },
        };

        var keys = MediaKey.For(item);

        keys.ShouldContain("movie:tmdb:45745");
        keys.ShouldContain("movie:imdb:tt1727587");
    }

    [Theory]
    [InlineData("1]/../../x")]
    [InlineData("a:b")]
    [InlineData("x\\y")]
    [InlineData("a?b")]
    [InlineData("a b")]
    [InlineData("ééé")]
    [InlineData("123456789012345678901234567890123")]
    public void MediaKey_InvalidIds_AreIgnored(string bad)
    {
        MediaKey.NormalizeId(bad).ShouldBeNull();
        MediaKey.For(Movie("X", tmdb: bad)).ShouldBeEmpty();
        MediaKey.For(Movie("X", imdb: bad)).ShouldBeEmpty();

        var onlyImdbSurvives = MediaKey.For(Movie("X", tmdb: bad, imdb: "tt1"));
        onlyImdbSurvives.ShouldBe(["movie:imdb:tt1"]);

        MediaKey.For(Episode("S", new() { ["Tvdb"] = bad, ["Tmdb"] = bad }, 1, 1)).ShouldBeEmpty();
        MediaKey.For(Episode("S", new() { ["Tvdb"] = bad, ["Tmdb"] = "7" }, 1, 1)).ShouldBe(["ep:tmdb:7:s1e1"]);
    }

    [Theory]
    [InlineData(" TT1727587 ", "tt1727587")]
    [InlineData("45745", "45745")]
    [InlineData("a_b-c", "a_b-c")]
    [InlineData("12345678901234567890123456789012", "12345678901234567890123456789012")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void NormalizeId_TrimsLowercasesAndValidates(string? input, string? expected)
        => MediaKey.NormalizeId(input).ShouldBe(expected);

    [Fact]
    public void Movie_BlankIds_AreIgnored()
    {
        var item = Movie("X", tmdb: "  ", imdb: "");

        MediaKey.For(item).ShouldBeEmpty();
    }

    [Fact]
    public void Movie_TvdbOnly_IsUnmatched()
    {
        var item = Movie("X") with { ProviderIds = new Dictionary<string, string> { ["Tvdb"] = "1" } };

        MediaKey.For(item).ShouldBeEmpty();
    }

    [Fact]
    public void Episode_KeyedBySeriesIdSeasonEpisode()
    {
        var item = Episode("Show", new Dictionary<string, string> { ["Tvdb"] = "153021" }, 2, 1);

        MediaKey.For(item).ShouldBe(new[] { "ep:tvdb:153021:s2e1" });
    }

    [Fact]
    public void Episode_WithTvdbAndTmdbSeries_HasBothKeys()
    {
        var item = Episode(
            "Show",
            new Dictionary<string, string> { ["tvdb"] = "153021", ["Tmdb"] = "99" },
            1,
            3);

        var keys = MediaKey.For(item);

        keys.Count.ShouldBe(2);
        keys.ShouldContain("ep:tvdb:153021:s1e3");
        keys.ShouldContain("ep:tmdb:99:s1e3");
    }

    [Fact]
    public void Episode_WithoutSeasonOrEpisodeNumber_IsUnmatched()
    {
        var ids = new Dictionary<string, string> { ["Tvdb"] = "153021" };

        MediaKey.For(Episode("Show", ids, null, 1)).ShouldBeEmpty();
        MediaKey.For(Episode("Show", ids, 1, null)).ShouldBeEmpty();
    }

    [Fact]
    public void Episode_WithoutSeriesIds_IsUnmatched()
    {
        MediaKey.For(Episode("Show", null, 1, 1)).ShouldBeEmpty();
        MediaKey.For(Episode("Show", new Dictionary<string, string>(), 1, 1)).ShouldBeEmpty();
    }

    [Fact]
    public void OtherTypes_AreUnmatched()
    {
        var item = Movie("X", tmdb: "1") with { Type = "Series" };

        MediaKey.For(item).ShouldBeEmpty();
    }
}
