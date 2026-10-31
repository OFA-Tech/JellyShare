using System.Text.Json;
using Jellyfin.Plugin.JellyShare.Configuration;
using Jellyfin.Plugin.JellyShare.Protocol;
using Jellyfin.Plugin.JellyShare.Provider;
using Xunit;

namespace Jellyfin.Plugin.JellyShare.Tests.Provider;

public class CatalogBuilderTests
{
    private const string MoviesRoot = "/data/placeholders/Movies";
    private const string ShowsRoot = "/data/placeholders/Shows";

    private static readonly Guid LibA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid LibB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly DateTime Modified = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private static CatalogItem Item(Guid id, string type = "Movie", DateTime? modified = null, string? seriesId = null)
        => new(
            id.ToString("N"),
            type,
            "Name " + id.ToString("N")[..4],
            2010,
            new Dictionary<string, string> { ["Tmdb"] = "1" },
            type == "Episode" ? "Show" : null,
            type == "Episode" ? new Dictionary<string, string> { ["Tvdb"] = "9" } : null,
            type == "Episode" ? 1 : null,
            type == "Episode" ? 2 : null,
            1000,
            null,
            new MediaInfo("mkv", "h264", 1920, 1080, 4000000, Array.Empty<AudioTrack>(), Array.Empty<SubtitleTrack>()),
            new[] { "Primary" },
            modified ?? Modified,
            seriesId);

    private static LocalItem Local(Guid id, string? path, string type = "Movie", DateTime? modified = null, string? seriesId = null)
        => new(id, type, path, Item(id, type, modified, seriesId));

    private static Guid G(int n) => new(n, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1);

    private static PeerShare Share(params Guid[] libs)
        => new() { Id = Guid.Parse("11111111-1111-1111-1111-111111111111"), Name = "bob", Enabled = true, LibraryIds = libs.ToList() };

    private static CatalogBuilder Builder(FakeSource source, Func<string, bool>? exists = null)
        => new(source, new FakeIdentity(), new FakeConfig(), exists ?? (_ => true));

    [Fact]
    public void Build_ExcludesPlaceholderPaths()
    {
        var source = new FakeSource();
        source.Items.Add((LibA, Local(G(1), "/media/Movies/A/A.mkv")));
        source.Items.Add((LibA, Local(G(2), MoviesRoot + "/B/B.mkv")));
        source.Items.Add((LibA, Local(G(3), ShowsRoot + "/S/Season 01/S.mkv", "Episode")));

        var catalog = Builder(source).Build(Share(LibA));

        Assert.Equal(new[] { G(1).ToString("N") }, catalog.Items.Select(i => i.Id));
    }

    [Fact]
    public void Build_ExcludesStrmFiles()
    {
        var source = new FakeSource();
        source.Items.Add((LibA, Local(G(1), "/media/Movies/A/A.mkv")));
        source.Items.Add((LibA, Local(G(2), "/media/Movies/B/B.STRM")));
        source.Items.Add((LibA, Local(G(3), "/media/Movies/C/C.strm")));

        var catalog = Builder(source).Build(Share(LibA));

        Assert.Equal(new[] { G(1).ToString("N") }, catalog.Items.Select(i => i.Id));
    }

    [Fact]
    public void Build_ExcludesNullOrEmptyPath()
    {
        var source = new FakeSource();
        source.Items.Add((LibA, Local(G(1), "/media/Movies/A/A.mkv")));
        source.Items.Add((LibA, Local(G(2), null)));
        source.Items.Add((LibA, Local(G(3), string.Empty)));

        var catalog = Builder(source).Build(Share(LibA));

        Assert.Single(catalog.Items);
    }

    [Fact]
    public void Build_ExcludesMissingFiles()
    {
        var source = new FakeSource();
        source.Items.Add((LibA, Local(G(1), "/media/Movies/A/A.mkv")));
        source.Items.Add((LibA, Local(G(2), "/media/Movies/B/B.mkv")));

        var catalog = Builder(source, p => !p.Contains("/B/")).Build(Share(LibA));

        Assert.Equal(new[] { G(1).ToString("N") }, catalog.Items.Select(i => i.Id));
    }

    [Fact]
    public void Build_OnlySharedLibraries()
    {
        var source = new FakeSource();
        source.Items.Add((LibA, Local(G(1), "/media/Movies/A/A.mkv")));
        source.Items.Add((LibB, Local(G(2), "/media/Other/B/B.mkv")));

        var catalog = Builder(source).Build(Share(LibA));

        Assert.Equal(new[] { G(1).ToString("N") }, catalog.Items.Select(i => i.Id));
    }

    [Fact]
    public void Build_NoLibraries_ReturnsEmptyCatalog()
    {
        var source = new FakeSource();
        source.Items.Add((LibA, Local(G(1), "/media/Movies/A/A.mkv")));

        var catalog = Builder(source).Build(Share());

        Assert.Empty(catalog.Items);
        Assert.Equal("1.0", catalog.ProtocolVersion);
        Assert.Equal("Alice", catalog.ServerName);
    }

    [Fact]
    public void Build_EpisodeCarriesSeriesNameProviderIdsAndSeriesId()
    {
        var seriesId = G(99).ToString("N");
        var source = new FakeSource();
        source.Items.Add((LibA, Local(G(1), "/media/Shows/S/Season 01/S.mkv", "Episode", seriesId: seriesId)));

        var item = Assert.Single(Builder(source).Build(Share(LibA)).Items);

        Assert.Equal("Episode", item.Type);
        Assert.Equal("Show", item.SeriesName);
        Assert.Equal("9", item.SeriesProviderIds!["Tvdb"]);
        Assert.Equal(seriesId, item.SeriesId);
    }

    [Fact]
    public void Build_EtagStableForSameInput()
    {
        var source = new FakeSource();
        source.Items.Add((LibA, Local(G(1), "/media/Movies/A/A.mkv")));
        source.Items.Add((LibA, Local(G(2), "/media/Movies/B/B.mkv")));

        var builder = Builder(source);
        var first = builder.Build(Share(LibA)).Etag;
        source.Items.Reverse();
        var second = builder.Build(Share(LibA)).Etag;

        Assert.Equal(first, second);
        Assert.Matches("^[0-9a-f]{64}$", first);
    }

    [Fact]
    public void Build_EtagChangesWhenItemModified()
    {
        var source = new FakeSource();
        source.Items.Add((LibA, Local(G(1), "/media/Movies/A/A.mkv")));
        var before = Builder(source).Build(Share(LibA)).Etag;

        source.Items.Clear();
        source.Items.Add((LibA, Local(G(1), "/media/Movies/A/A.mkv", modified: Modified.AddMinutes(1))));
        var after = Builder(source).Build(Share(LibA)).Etag;

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Build_EtagIgnoresExcludedItems()
    {
        var source = new FakeSource();
        source.Items.Add((LibA, Local(G(1), "/media/Movies/A/A.mkv")));
        var before = Builder(source).Build(Share(LibA)).Etag;

        source.Items.Add((LibA, Local(G(2), MoviesRoot + "/B/B.mkv")));
        var after = Builder(source).Build(Share(LibA)).Etag;

        Assert.Equal(before, after);
    }

    [Fact]
    public void Build_SerializesToCamelCaseWireShape()
    {
        var source = new FakeSource();
        source.Items.Add((LibA, Local(G(1), "/media/Movies/A/A.mkv")));

        var json = JsonSerializer.Serialize(Builder(source).Build(Share(LibA)));

        Assert.Contains("\"protocolVersion\":\"1.0\"", json);
        Assert.Contains("\"serverName\":\"Alice\"", json);
        Assert.Contains("\"etag\":", json);
        Assert.Contains("\"items\":[{\"id\":", json);
    }

    [Fact]
    public void IsExposed_TrueForCatalogItemsAndTheirSeries_FalseOtherwise()
    {
        var seriesId = G(99);
        var source = new FakeSource();
        source.Items.Add((LibA, Local(G(1), "/media/Movies/A/A.mkv")));
        source.Items.Add((LibA, Local(G(2), "/media/Shows/S/Season 01/S.mkv", "Episode", seriesId: seriesId.ToString("N"))));
        source.Items.Add((LibB, Local(G(3), "/media/Other/B/B.mkv")));
        var builder = Builder(source);
        var share = Share(LibA);

        Assert.True(builder.IsExposed(share, G(1)));
        Assert.True(builder.IsExposed(share, G(2)));
        Assert.True(builder.IsExposed(share, seriesId));
        Assert.False(builder.IsExposed(share, G(3)));
        Assert.False(builder.IsExposed(share, Guid.NewGuid()));
    }
}
