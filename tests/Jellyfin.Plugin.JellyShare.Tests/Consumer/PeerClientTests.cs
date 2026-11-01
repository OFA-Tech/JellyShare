using System.Net;
using System.Text;
using Jellyfin.Plugin.JellyShare.Configuration;
using Jellyfin.Plugin.JellyShare.Consumer;
using Xunit;

namespace Jellyfin.Plugin.JellyShare.Tests.Consumer;

public class PeerClientTests
{
    private const string Token = "js1_SECRET-token-value";

    private const string MinimalCatalog = """
        {"protocolVersion":"1.0","serverName":"alice","etag":"abc","items":[
          {"id":"m1","type":"Movie","name":"Big Buck Bunny","dateModified":"2026-10-31T00:00:00Z"},
          {"id":"e1","type":"Episode","name":"Ep","seriesName":"Pioneer One","seriesProviderIds":{"Tvdb":"153021"},
           "seasonNumber":1,"episodeNumber":1,"seriesId":"s1","media":{"container":"mkv","bitRate":1312543},
           "providerIds":{},"imageTypes":["Primary"],"dateModified":"2026-10-31T00:00:00Z"},
          {"type":"Movie","name":"no id"}
        ]}
        """;

    private static RemotePeer Peer(string baseUrl = "https://h/jellyfin")
        => new() { Id = Guid.NewGuid(), Name = "alice", BaseUrl = baseUrl, Token = Token };

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (PeerClient Client, FakeHandler Handler) Create(FakeHandler handler, TimeSpan? timeout = null)
        => (new PeerClient(() => new HttpClient(handler, false), timeout), handler);

    [Theory]
    [InlineData("https://h/jellyfin", "https://h/jellyfin/JellyShare/v1/catalog")]
    [InlineData("https://h/jellyfin/", "https://h/jellyfin/JellyShare/v1/catalog")]
    [InlineData("http://h:8101", "http://h:8101/JellyShare/v1/catalog")]
    [InlineData(" http://h:8101/ ", "http://h:8101/JellyShare/v1/catalog")]
    public async Task PeerClient_JoinsBaseUrlKeepingSubPath(string baseUrl, string expected)
    {
        var (client, handler) = Create(new FakeHandler(_ => Json(MinimalCatalog)));
        var result = await client.FetchCatalogAsync(Peer(baseUrl), null, CancellationToken.None);
        Assert.True(result.Ok, result.Error);
        Assert.Equal(expected, handler.Requests.Single().RequestUri!.ToString());
    }

    [Theory]
    [InlineData("ftp://h/x")]
    [InlineData("not a url")]
    [InlineData("")]
    public async Task PeerClient_InvalidBaseUrl_IsError(string baseUrl)
    {
        var (client, handler) = Create(new FakeHandler(_ => Json(MinimalCatalog)));
        var result = await client.FetchCatalogAsync(Peer(baseUrl), null, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal("invalid base url", result.Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PeerClient_SendsTokenOnlyInHeader()
    {
        var (client, handler) = Create(new FakeHandler(_ => Json(MinimalCatalog)));
        await client.FetchCatalogAsync(Peer(), null, CancellationToken.None);
        await client.GetImageAsync(Peer(), "abc", "Primary", CancellationToken.None);
        using var _ = await client.OpenStreamAsync(Peer(), "abc", null, CancellationToken.None);

        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, r =>
        {
            Assert.Equal(Token, r.Headers.GetValues("X-JellyShare-Token").Single());
            Assert.DoesNotContain(Token, r.RequestUri!.ToString(), StringComparison.Ordinal);
        });
        Assert.Equal("https://h/jellyfin/JellyShare/v1/image/abc/Primary", handler.Requests[1].RequestUri!.ToString());
        Assert.Equal("https://h/jellyfin/JellyShare/v1/stream/abc", handler.Requests[2].RequestUri!.ToString());
    }

    [Fact]
    public async Task PeerClient_ToleratesOmittedNullFields()
    {
        var (client, _) = Create(new FakeHandler(_ => Json(MinimalCatalog)));
        var result = await client.FetchCatalogAsync(Peer(), null, CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        var items = result.Catalog!.Items;
        Assert.Equal(2, items.Count);
        var movie = items[0];
        Assert.Empty(movie.ProviderIds);
        Assert.Empty(movie.ImageTypes);
        Assert.NotNull(movie.Media);
        Assert.Empty(movie.Media.Audio);
        Assert.Empty(movie.Media.Subtitles);
        Assert.Null(movie.Year);
        Assert.Equal("s1", items[1].SeriesId);
        Assert.Equal(1312543, items[1].Media.BitRate);
        Assert.Equal("abc", result.Catalog.Etag);
    }

    [Fact]
    public async Task PeerClient_SendsIfNoneMatch_And304IsNotModified()
    {
        var (client, handler) = Create(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotModified)));
        var result = await client.FetchCatalogAsync(Peer(), "abc", CancellationToken.None);

        Assert.True(result.NotModified);
        Assert.Null(result.Catalog);
        Assert.Equal("\"abc\"", handler.Requests.Single().Headers.IfNoneMatch.Single().ToString());
    }

    [Fact]
    public async Task PeerClient_Non2xx_IsErrorWithoutToken()
    {
        var (client, _) = Create(new FakeHandler(_ => Json("{\"error\":\"" + Token + "\"}", HttpStatusCode.Unauthorized)));
        var result = await client.FetchCatalogAsync(Peer(), null, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal("HTTP 401", result.Error);
    }

    [Theory]
    [InlineData("2.0", "incompatible protocol 2.0")]
    [InlineData("0.9", "incompatible protocol 0.9")]
    [InlineData("x", "incompatible protocol x")]
    public async Task PeerClient_IncompatibleProtocol_IsError(string version, string expected)
    {
        var body = MinimalCatalog.Replace("\"1.0\"", "\"" + version + "\"", StringComparison.Ordinal);
        var (client, _) = Create(new FakeHandler(_ => Json(body)));
        var result = await client.FetchCatalogAsync(Peer(), null, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal(expected, result.Error);
    }

    [Fact]
    public async Task PeerClient_MinorVersionBump_IsAccepted()
    {
        var body = MinimalCatalog.Replace("\"1.0\"", "\"1.7\"", StringComparison.Ordinal);
        var (client, _) = Create(new FakeHandler(_ => Json(body)));
        Assert.True((await client.FetchCatalogAsync(Peer(), null, CancellationToken.None)).Ok);
    }

    [Fact]
    public async Task PeerClient_Timeout_IsError()
    {
        var (client, _) = Create(
            new FakeHandler(async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return Json(MinimalCatalog);
            }),
            TimeSpan.FromMilliseconds(100));
        var result = await client.FetchCatalogAsync(Peer(), null, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal("timeout", result.Error);
        Assert.Equal(TimeSpan.FromSeconds(30), PeerClient.DefaultTimeout);
    }

    [Fact]
    public async Task PeerClient_ConnectionError_DoesNotLeakToken()
    {
        var (client, _) = Create(new FakeHandler(_ => throw new HttpRequestException("refused with " + Token)));
        var result = await client.FetchCatalogAsync(Peer(), null, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.DoesNotContain(Token, result.Error, StringComparison.Ordinal);
        Assert.StartsWith("connection failed", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PeerClient_InvalidJson_IsError()
    {
        var (client, _) = Create(new FakeHandler(_ => Json("<html>")));
        var result = await client.FetchCatalogAsync(Peer(), null, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal("invalid catalog", result.Error);
    }

    [Fact]
    public async Task PeerClient_OpenStream_ForwardsRange()
    {
        var (client, handler) = Create(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.PartialContent)));
        using var response = await client.OpenStreamAsync(Peer(), "abc", "bytes=100-", CancellationToken.None);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bytes=100-", handler.Requests.Single().Headers.Range!.ToString());
    }

    [Fact]
    public async Task PeerClient_GetImage_ReturnsBytesOrNull()
    {
        var (client, _) = Create(new FakeHandler(r => r.RequestUri!.AbsolutePath.EndsWith("/Primary", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) }
            : new HttpResponseMessage(HttpStatusCode.NotFound)));

        await using var found = await client.GetImageAsync(Peer(), "abc", "Primary", CancellationToken.None);
        Assert.NotNull(found);
        using var ms = new MemoryStream();
        await found.CopyToAsync(ms);
        Assert.Equal([1, 2, 3], ms.ToArray());
        Assert.Null(await client.GetImageAsync(Peer(), "abc", "Backdrop", CancellationToken.None));
    }
}
