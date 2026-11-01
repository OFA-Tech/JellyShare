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

    private int _materialized;

    private LocalItem Local(Guid id, string? path, string type = "Movie", DateTime? modified = null, string? seriesId = null)
        => new(
            id,
            type,
            path,
            seriesId is null ? null : Guid.Parse(seriesId),
            () =>
            {
                Interlocked.Increment(ref _materialized);
                return Item(id, type, modified, seriesId);
            })
        {
            DateModified = modified ?? Modified,
        };

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

    [Fact]
    public void Build_DoesNotMaterializeExcludedItems()
    {
        var source = new FakeSource();
        source.Items.Add((LibA, Local(G(1), "/media/Movies/A/A.mkv")));
        source.Items.Add((LibA, Local(G(2), MoviesRoot + "/B/B.mkv")));
        source.Items.Add((LibA, Local(G(3), "/media/Movies/C/C.strm")));
        source.Items.Add((LibA, Local(G(4), null)));
        source.Items.Add((LibA, Local(G(5), "/media/Movies/missing/M.mkv")));

        var catalog = Builder(source, p => !p.Contains("/missing/")).Build(Share(LibA));

        Assert.Single(catalog.Items);
        Assert.Equal(1, _materialized);
    }

    [Fact]
    public void IsExposed_NeverMaterializesCatalogItems()
    {
        var source = new FakeSource();
        source.Items.Add((LibA, Local(G(1), "/media/Movies/A/A.mkv")));
        source.Items.Add((LibA, Local(G(2), "/media/Shows/S/Season 01/S.mkv", "Episode", seriesId: G(99).ToString("N"))));

        var builder = Builder(source);

        Assert.True(builder.IsExposed(Share(LibA), G(1)));
        Assert.True(builder.IsExposed(Share(LibA), G(99)));
        Assert.Equal(0, _materialized);
    }

    [Fact]
    public void IsExposed_SeriesWhoseEpisodesArePlaceholdersOrUnshared_IsNotExposed()
    {
        var onlyPlaceholders = G(90);
        var otherLibrary = G(91);
        var source = new FakeSource();
        source.Items.Add((LibA, Local(G(1), ShowsRoot + "/P/Season 01/P.strm", "Episode", seriesId: onlyPlaceholders.ToString("N"))));
        source.Items.Add((LibA, Local(G(2), ShowsRoot + "/P/Season 01/P2.mkv", "Episode", seriesId: onlyPlaceholders.ToString("N"))));
        source.Items.Add((LibB, Local(G(3), "/media/Other/S/Season 01/S.mkv", "Episode", seriesId: otherLibrary.ToString("N"))));
        var builder = Builder(source);

        Assert.False(builder.IsExposed(Share(LibA), onlyPlaceholders));
        Assert.False(builder.IsExposed(Share(LibA), otherLibrary));
    }

    [Fact]
    public void IsExposed_ChecksOnlyTheRequestedItem_NeverEnumeratesTheLibrary()
    {
        var source = new FakeSource();
        source.Items.Add((LibA, Local(G(1), "/media/Movies/A/A.mkv")));
        source.Items.Add((LibA, Local(G(2), "/media/Movies/missing/B.mkv")));
        source.Items.Add((LibA, Local(G(3), "/media/Shows/S/Season 01/S.mkv", "Episode", seriesId: G(99).ToString("N"))));
        var builder = Builder(source, p => !p.Contains("/missing/"));
        var share = Share(LibA);

        Assert.True(builder.IsExposed(share, G(1)));
        Assert.False(builder.IsExposed(share, G(2)));
        Assert.True(builder.IsExposed(share, G(99)));

        Assert.Equal(0, source.GetItemsCalls);
        Assert.Equal(3, source.CandidateCalls);
    }

    [Fact]
    public void IsExposed_NoLibraries_IsFalseWithoutQuery()
    {
        var source = new FakeSource();
        source.Items.Add((LibA, Local(G(1), "/media/Movies/A/A.mkv")));

        Assert.False(Builder(source).IsExposed(Share(), G(1)));
        Assert.Equal(0, source.CandidateCalls);
    }

    [Fact]
    public void Plan_EtagIsComputedWithoutMaterializingItems()
    {
        var source = new FakeSource();
        source.Items.Add((LibA, Local(G(1), "/media/Movies/A/A.mkv")));
        source.Items.Add((LibA, Local(G(2), "/media/Movies/B/B.mkv")));
        var builder = Builder(source);

        var plan = builder.Plan(Share(LibA));

        Assert.Matches("^[0-9a-f]{64}$", plan.Etag);
        Assert.Equal(0, _materialized);
        Assert.Equal(plan.Etag, plan.Build().Etag);
        Assert.Equal(2, _materialized);
    }

    [Fact]
    public void Build_SameEtagTwice_IsServedFromCache()
    {
        var source = new FakeSource();
        source.Items.Add((LibA, Local(G(1), "/media/Movies/A/A.mkv")));
        source.Items.Add((LibA, Local(G(2), "/media/Movies/B/B.mkv")));
        var builder = Builder(source);

        var first = builder.Build(Share(LibA));
        var second = builder.Build(Share(LibA));

        Assert.Same(first, second);
        Assert.Equal(2, _materialized);
    }

    [Fact]
    public void Build_ChangedItem_RebuildsInsteadOfServingCache()
    {
        var source = new FakeSource();
        source.Items.Add((LibA, Local(G(1), "/media/Movies/A/A.mkv")));
        var builder = Builder(source);
        var first = builder.Build(Share(LibA));

        source.Items.Clear();
        source.Items.Add((LibA, Local(G(1), "/media/Movies/A/A.mkv", modified: Modified.AddMinutes(1))));
        var second = builder.Build(Share(LibA));

        Assert.NotEqual(first.Etag, second.Etag);
        Assert.Equal(Modified.AddMinutes(1), Assert.Single(second.Items).DateModified);
    }
}