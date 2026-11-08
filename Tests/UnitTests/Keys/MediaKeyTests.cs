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

    private const string Mbid = "b9f3a1c2-5d4e-4f6a-8b7c-0123456789ab";

    [Fact]
    public void NormalizeId_AcceptsMusicBrainzUuid()
    {
        MediaKey.NormalizeId(" " + Mbid.ToUpperInvariant() + " ").ShouldBe(Mbid);
        MediaKey.NormalizeId(new string('a', 36)).ShouldBe(new string('a', 36));
    }

    [Theory]
    [InlineData("b9f3a1c2-5d4e-4f6a-8b7c-0123456789ag")]
    [InlineData("b9f3a1c2-5d4e-4f6a-8b7c-0123456789abc")]
    [InlineData("b9f3a1c2_5d4e_4f6a_8b7c_0123456789ab")]
    public void NormalizeId_RejectsOtherLongValues(string bad)
        => MediaKey.NormalizeId(bad).ShouldBeNull();

    [Fact]
    public void For_Audio_WithMbid_HasMbAndNameKeys()
    {
        var keys = MediaKey.For(Track("Hells Bells", mbid: Mbid));

        keys.Count.ShouldBe(2);
        keys[0].ShouldBe("track:mb:" + Mbid);
        keys[1].ShouldStartWith("track:name:");
        keys[1].Length.ShouldBe("track:name:".Length + 16);
    }

    [Fact]
    public void For_Audio_WithoutMbid_HasOnlyNameKey()
        => MediaKey.For(Track("Hells Bells")).ShouldHaveSingleItem().ShouldStartWith("track:name:");

    [Fact]
    public void For_Audio_IgnoresReleaseTrackId()
    {
        // Release-track ids differ per edition: matching on them would turn one recording into a false conflict.
        var item = Track("Hells Bells") with { ProviderIds = new() { ["MusicBrainzTrack"] = Mbid } };

        MediaKey.For(item).ShouldHaveSingleItem().ShouldStartWith("track:name:");
    }

    [Fact]
    public void For_Audio_TitleOfOnlySymbols_HasNoKey()
        => MediaKey.For(Track("?!…", mbid: Mbid)).ShouldBeEmpty();

    [Fact]
    public void For_AudioBook_NameOfOnlySymbols_HasNoKey()
        => MediaKey.For(Book("--")).ShouldBeEmpty();

    [Fact]
    public void For_Audio_NameKeyIgnoresCaseAndPunctuation()
    {
        var a = MediaKey.For(Track("Hells Bells", albumArtist: "AC/DC"));
        var b = MediaKey.For(Track("hells  bells!", albumArtist: "ac dc"));

        a.ShouldBe(b);
    }

    [Fact]
    public void For_Audio_NameKeyIgnoresDiacritics()
        => MediaKey.For(Track("Déjà Vu")).ShouldBe(MediaKey.For(Track("deja vu")));

    [Fact]
    public void For_Audio_DifferentTrackNumber_DifferentKey()
        => MediaKey.For(Track("Intro", track: 1)).ShouldNotBe(MediaKey.For(Track("Intro", track: 2)));

    [Fact]
    public void For_Audio_WithoutTitle_HasNoKey()
        => MediaKey.For(Track("  ", mbid: Mbid)).ShouldBeEmpty();

    [Fact]
    public void For_Audio_NameKey_IsFirst16HexOfSha256OfNormalizedFields()
    {
        var expected = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("ac dc|back in black|1|1|hells bells")))[..16];

        MediaKey.For(Track("Hells Bells")).ShouldBe(["track:name:" + expected]);
    }

    [Fact]
    public void For_AudioBook_KeyFromFolderAndName()
    {
        var keys = MediaKey.For(Book("Part 1"));

        keys.ShouldHaveSingleItem().ShouldStartWith("book:name:");
        keys.ShouldBe(MediaKey.For(Book("part 1", folderPath: "author x/BOOK y")));
        keys.ShouldNotBe(MediaKey.For(Book("Part 1", folderPath: "Author X/Other")));
    }

    [Fact]
    public void For_AudioBook_WithoutName_HasNoKey()
        => MediaKey.For(Book("")).ShouldBeEmpty();

    [Fact]
    public void OtherTypes_AreUnmatched()
    {
        var item = Movie("X", tmdb: "1") with { Type = "Series" };

        MediaKey.For(item).ShouldBeEmpty();
    }
}
