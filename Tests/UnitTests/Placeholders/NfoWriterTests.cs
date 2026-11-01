using System.Text;
using System.Xml.Linq;
using JellyShare.Application.Features.Placeholders;
using JellyShare.Domain.Models.Library;
using JellyShare.Domain.Models.Protocol;
using JellyShare.UnitTests.Fakes;

namespace JellyShare.UnitTests.Placeholders;

public sealed class NfoWriterTests
{
    private static readonly MediaInfo _media = new(
        "mkv", "h264", 1920, 1080, 5_000_000,
        [new AudioTrack("eng", "aac", 2, "English"), new AudioTrack("por", "ac3", 6, null)],
        [new SubtitleTrack("eng", "srt", null)]);

    private static CatalogItem Sintel() =>
        TestItems.Movie("Sintel", tmdb: "45745", imdb: "tt1727587") with
        {
            Year = 2010,
            Overview = "A girl & her <dragon>.",
            RunTimeTicks = 52L * 600_000_000L + 100,
            Media = _media,
        };

    [Fact]
    public void Nfo_Movie_HasDefaultTmdbUniqueIdAndStreamDetails()
    {
        var xml = XDocument.Parse(NfoWriter.Movie(Sintel()));
        var root = xml.Root!;
        root.Name.LocalName.ShouldBe("movie");
        root.Element("title")!.Value.ShouldBe("Sintel");
        root.Element("year")!.Value.ShouldBe("2010");
        root.Element("plot")!.Value.ShouldBe("A girl & her <dragon>.");
        root.Element("runtime")!.Value.ShouldBe("52");

        var tmdb = root.Elements("uniqueid").Single(e => (string?)e.Attribute("type") == "tmdb");
        ((string?)tmdb.Attribute("default")).ShouldBe("true");
        tmdb.Value.ShouldBe("45745");
        var imdb = root.Elements("uniqueid").Single(e => (string?)e.Attribute("type") == "imdb");
        imdb.Attribute("default").ShouldBeNull();

        var details = root.Element("fileinfo")!.Element("streamdetails")!;
        var video = details.Element("video")!;
        video.Element("codec")!.Value.ShouldBe("h264");
        video.Element("width")!.Value.ShouldBe("1920");
        video.Element("height")!.Value.ShouldBe("1080");
        var audio = details.Elements("audio").ToList();
        audio.Count.ShouldBe(2);
        audio[0].Element("codec")!.Value.ShouldBe("aac");
        audio[0].Element("language")!.Value.ShouldBe("eng");
        audio[1].Element("channels")!.Value.ShouldBe("6");
        details.Element("subtitle")!.Element("language")!.Value.ShouldBe("eng");
    }

    [Fact]
    public void Nfo_PeerIdsFailingValidation_AreNotWritten()
    {
        var movie = TestItems.Movie("X", tmdb: "1]/../x", imdb: "tt5");
        var ids = XDocument.Parse(NfoWriter.Movie(movie)).Root!.Elements("uniqueid").Select(e => e.Value).ToList();
        ids.ShouldBe(["tt5"]);

        var ep = TestItems.Episode("S", new() { ["Tvdb"] = "a:b", ["Tmdb"] = "7" }, 1, 1);
        var series = XDocument.Parse(NfoWriter.Series(ep)).Root!.Elements("uniqueid").Select(e => e.Value).ToList();
        series.ShouldBe(["7"]);
    }

    [Fact]
    public void Nfo_Series_UsesGivenMergedSeriesIds()
    {
        var ep = TestItems.Episode("S", new() { ["Tvdb"] = "1" }, 1, 1);
        var merged = new Dictionary<string, string> { ["tvdb"] = "1", ["imdb"] = "tt2" };
        var ids = XDocument.Parse(NfoWriter.Series(ep, null, merged)).Root!.Elements("uniqueid")
            .ToDictionary(e => (string)e.Attribute("type")!, e => e.Value);
        ids["imdb"].ShouldBe("tt2");
        ids["tvdb"].ShouldBe("1");
    }

    [Fact]
    public void Nfo_Movie_ImdbIsDefaultWhenNoTmdb()
    {
        var xml = XDocument.Parse(NfoWriter.Movie(TestItems.Movie("X", imdb: "tt5")));
        var id = xml.Root!.Elements("uniqueid").Single();
        ((string?)id.Attribute("type")).ShouldBe("imdb");
        ((string?)id.Attribute("default")).ShouldBe("true");
    }

    [Fact]
    public void Nfo_IsDeterministic_Utf8WithoutBom_AndStripsInvalidXmlChars()
    {
        var item = Sintel() with { Overview = "bad\u0001char é" };
        var a = NfoWriter.Movie(item);
        var b = NfoWriter.Movie(item);
        b.ShouldBe(a);
        a.StartsWith('\uFEFF').ShouldBeFalse();
        XDocument.Parse(a).Root!.Element("plot")!.Value.ShouldBe("badchar é");
        a.ShouldNotContain("utf-16", Case.Insensitive);
        Encoding.UTF8.GetBytes(a)[0].ShouldNotBe((byte)0xEF);
    }

    [Fact]
    public void Nfo_Episode_HasSeasonEpisodeAndShowTitle()
    {
        var item = TestItems.Episode("Pioneer One", new() { ["Tvdb"] = "153021" }, 2, 5) with
        {
            Name = "Alone",
            ProviderIds = new() { ["Tvdb"] = "999" },
        };
        var root = XDocument.Parse(NfoWriter.Episode(item)).Root!;
        root.Name.LocalName.ShouldBe("episodedetails");
        root.Element("title")!.Value.ShouldBe("Alone");
        root.Element("showtitle")!.Value.ShouldBe("Pioneer One");
        root.Element("season")!.Value.ShouldBe("2");
        root.Element("episode")!.Value.ShouldBe("5");

        var overridden = XDocument.Parse(NfoWriter.Episode(item, "Owner Name")).Root!;
        overridden.Element("showtitle")!.Value.ShouldBe("Owner Name");
    }

    [Fact]
    public void Nfo_Series_WritesAllUniqueIds()
    {
        var item = TestItems.Episode(
            "Pioneer One",
            new() { ["Tvdb"] = "153021", ["Imdb"] = "tt1759761", ["Tmdb"] = "77" },
            1,
            1);
        var root = XDocument.Parse(NfoWriter.Series(item)).Root!;
        root.Name.LocalName.ShouldBe("tvshow");
        root.Element("title")!.Value.ShouldBe("Pioneer One");
        var ids = root.Elements("uniqueid").ToDictionary(e => (string)e.Attribute("type")!, e => e.Value);
        ids["tvdb"].ShouldBe("153021");
        ids["imdb"].ShouldBe("tt1759761");
        ids["tmdb"].ShouldBe("77");
        ((string?)root.Elements("uniqueid").First().Attribute("default")).ShouldBe("true");
    }

    [Fact]
    public void Nfo_Series_UsesLocalSeriesIdsAndTitle()
    {
        var item = TestItems.Episode("Remote Name", new() { ["Tmdb"] = "77" }, 1, 1);
        var local = new LocalSeries(
            "Owner Name",
            new Dictionary<string, string> { ["Tvdb"] = "153021", ["Imdb"] = "tt1759761" });
        var root = XDocument.Parse(NfoWriter.Series(item, local)).Root!;
        root.Element("title")!.Value.ShouldBe("Owner Name");
        var ids = root.Elements("uniqueid").ToDictionary(e => (string)e.Attribute("type")!, e => e.Value);
        ids["tvdb"].ShouldBe("153021");
        ids["imdb"].ShouldBe("tt1759761");
        ids.ContainsKey("tmdb").ShouldBeFalse();
    }
}
