using System.Text;
using System.Xml.Linq;
using Jellyfin.Plugin.JellyShare.Placeholders;
using Jellyfin.Plugin.JellyShare.Protocol;
using Jellyfin.Plugin.JellyShare.Tests.Keys;

using Xunit;

namespace Jellyfin.Plugin.JellyShare.Tests.Placeholders;

public class NfoWriterTests
{
    private static readonly MediaInfo Media = new(
        "mkv", "h264", 1920, 1080, 5_000_000,
        [new AudioTrack("eng", "aac", 2, "English"), new AudioTrack("por", "ac3", 6, null)],
        [new SubtitleTrack("eng", "srt", null)]);

    private static CatalogItem Sintel() =>
        TestItems.Movie("Sintel", tmdb: "45745", imdb: "tt1727587") with
        {
            Year = 2010,
            Overview = "A girl & her <dragon>.",
            RunTimeTicks = 52L * 600_000_000L + 100,
            Media = Media,
        };

    [Fact]
    public void Nfo_Movie_HasDefaultTmdbUniqueIdAndStreamDetails()
    {
        var xml = XDocument.Parse(NfoWriter.Movie(Sintel()));
        var root = xml.Root!;
        Assert.Equal("movie", root.Name.LocalName);
        Assert.Equal("Sintel", root.Element("title")!.Value);
        Assert.Equal("2010", root.Element("year")!.Value);
        Assert.Equal("A girl & her <dragon>.", root.Element("plot")!.Value);
        Assert.Equal("52", root.Element("runtime")!.Value);

        var tmdb = root.Elements("uniqueid").Single(e => (string?)e.Attribute("type") == "tmdb");
        Assert.Equal("true", (string?)tmdb.Attribute("default"));
        Assert.Equal("45745", tmdb.Value);
        var imdb = root.Elements("uniqueid").Single(e => (string?)e.Attribute("type") == "imdb");
        Assert.Null(imdb.Attribute("default"));

        var details = root.Element("fileinfo")!.Element("streamdetails")!;
        var video = details.Element("video")!;
        Assert.Equal("h264", video.Element("codec")!.Value);
        Assert.Equal("1920", video.Element("width")!.Value);
        Assert.Equal("1080", video.Element("height")!.Value);
        var audio = details.Elements("audio").ToList();
        Assert.Equal(2, audio.Count);
        Assert.Equal("aac", audio[0].Element("codec")!.Value);
        Assert.Equal("eng", audio[0].Element("language")!.Value);
        Assert.Equal("6", audio[1].Element("channels")!.Value);
        Assert.Equal("eng", details.Element("subtitle")!.Element("language")!.Value);
    }

    [Fact]
    public void Nfo_Movie_ImdbIsDefaultWhenNoTmdb()
    {
        var xml = XDocument.Parse(NfoWriter.Movie(TestItems.Movie("X", imdb: "tt5")));
        var id = xml.Root!.Elements("uniqueid").Single();
        Assert.Equal("imdb", (string?)id.Attribute("type"));
        Assert.Equal("true", (string?)id.Attribute("default"));
    }

    [Fact]
    public void Nfo_IsDeterministic_Utf8WithoutBom_AndStripsInvalidXmlChars()
    {
        var item = Sintel() with { Overview = "bad\u0001char é" };
        var a = NfoWriter.Movie(item);
        var b = NfoWriter.Movie(item);
        Assert.Equal(a, b);
        Assert.False(a.StartsWith('\uFEFF'));
        Assert.Equal("badchar é", XDocument.Parse(a).Root!.Element("plot")!.Value);
        Assert.DoesNotContain("utf-16", a, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(0xEF, Encoding.UTF8.GetBytes(a)[0]);
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
        Assert.Equal("episodedetails", root.Name.LocalName);
        Assert.Equal("Alone", root.Element("title")!.Value);
        Assert.Equal("Pioneer One", root.Element("showtitle")!.Value);
        Assert.Equal("2", root.Element("season")!.Value);
        Assert.Equal("5", root.Element("episode")!.Value);

        var overridden = XDocument.Parse(NfoWriter.Episode(item, "Owner Name")).Root!;
        Assert.Equal("Owner Name", overridden.Element("showtitle")!.Value);
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
        Assert.Equal("tvshow", root.Name.LocalName);
        Assert.Equal("Pioneer One", root.Element("title")!.Value);
        var ids = root.Elements("uniqueid").ToDictionary(e => (string)e.Attribute("type")!, e => e.Value);
        Assert.Equal("153021", ids["tvdb"]);
        Assert.Equal("tt1759761", ids["imdb"]);
        Assert.Equal("77", ids["tmdb"]);
        Assert.Equal("true", (string?)root.Elements("uniqueid").First().Attribute("default"));
    }

    [Fact]
    public void Nfo_Series_UsesLocalSeriesIdsAndTitle()
    {
        var item = TestItems.Episode("Remote Name", new() { ["Tmdb"] = "77" }, 1, 1);
        var local = new LocalSeries(
            "Owner Name",
            new Dictionary<string, string> { ["Tvdb"] = "153021", ["Imdb"] = "tt1759761" });
        var root = XDocument.Parse(NfoWriter.Series(item, local)).Root!;
        Assert.Equal("Owner Name", root.Element("title")!.Value);
        var ids = root.Elements("uniqueid").ToDictionary(e => (string)e.Attribute("type")!, e => e.Value);
        Assert.Equal("153021", ids["tvdb"]);
        Assert.Equal("tt1759761", ids["imdb"]);
        Assert.False(ids.ContainsKey("tmdb"));
    }
}
