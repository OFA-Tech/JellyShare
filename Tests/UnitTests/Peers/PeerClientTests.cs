using System.Net;
using System.Text;
using JellyShare.Data.Peers.Clients;
using JellyShare.Domain.Enums;
using JellyShare.Domain.Models.Configuration;
using JellyShare.UnitTests.Fakes;

namespace JellyShare.UnitTests.Peers;

public sealed class PeerClientTests
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

    [Fact]
    public async Task FetchCatalog_SendsAcceptedSupportedTypes()
    {
        var (client, handler) = Create(new FakeHandler(_ => Json(MinimalCatalog)));
        var peer = Peer();
        await client.FetchCatalogAsync(peer, null, CancellationToken.None);
        peer.AcceptAllTypes = false;
        peer.AcceptTypes = [MediaType.Shows, MediaType.Music, MediaType.Movies];
        await client.FetchCatalogAsync(peer, null, CancellationToken.None);

        // All accepted, and Music accepted but not supported yet: both ask for Movies,Shows.
        handler.Requests.Select(r => r.RequestUri!.Query).ShouldBe(["?types=Movies,Shows", "?types=Movies,Shows"]);
    }

    [Fact]
    public async Task FetchCatalog_NothingSupportedAccepted_RequestsNothingAndReturnsEmptyCatalog()
    {
        var (client, handler) = Create(new FakeHandler(_ => Json(MinimalCatalog)));
        var peer = Peer();
        peer.AcceptAllTypes = false;
        peer.AcceptTypes = [MediaType.Music];

        var result = await client.FetchCatalogAsync(peer, "abc", CancellationToken.None);

        result.Ok.ShouldBeTrue(result.Error);
        result.Catalog!.Items.ShouldBeEmpty();
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task FetchCatalog_DropsItemsOfTypesNotAccepted()
    {
        var (client, _) = Create(new FakeHandler(_ => Json(MinimalCatalog)));
        var peer = Peer();
        peer.AcceptAllTypes = false;
        peer.AcceptTypes = [MediaType.Movies];

        var result = await client.FetchCatalogAsync(peer, null, CancellationToken.None);

        result.Catalog!.Items.Select(i => i.Id).ShouldBe(["m1"]);
    }

    [Theory]
    [InlineData("https://h/jellyfin", "https://h/jellyfin/JellyShare/v1/catalog?types=Movies,Shows")]
    [InlineData("https://h/jellyfin/", "https://h/jellyfin/JellyShare/v1/catalog?types=Movies,Shows")]
    [InlineData("http://h:8101", "http://h:8101/JellyShare/v1/catalog?types=Movies,Shows")]
    [InlineData(" http://h:8101/ ", "http://h:8101/JellyShare/v1/catalog?types=Movies,Shows")]
    public async Task PeerClient_JoinsBaseUrlKeepingSubPath(string baseUrl, string expected)
    {
        var (client, handler) = Create(new FakeHandler(_ => Json(MinimalCatalog)));
        var result = await client.FetchCatalogAsync(Peer(baseUrl), null, CancellationToken.None);
        result.Ok.ShouldBeTrue(result.Error);
        handler.Requests.Single().RequestUri!.ToString().ShouldBe(expected);
    }

    [Theory]
    [InlineData("ftp://h/x")]
    [InlineData("not a url")]
    [InlineData("")]
    public async Task PeerClient_InvalidBaseUrl_IsError(string baseUrl)
    {
        var (client, handler) = Create(new FakeHandler(_ => Json(MinimalCatalog)));
        var result = await client.FetchCatalogAsync(Peer(baseUrl), null, CancellationToken.None);
        result.Ok.ShouldBeFalse();
        result.Error.ShouldBe("invalid base url");
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task PeerClient_SendsTokenOnlyInHeader()
    {
        var (client, handler) = Create(new FakeHandler(_ => Json(MinimalCatalog)));
        await client.FetchCatalogAsync(Peer(), null, CancellationToken.None);
        await client.GetImageAsync(Peer(), "abc", "Primary", CancellationToken.None);
        using var _ = await client.OpenStreamAsync(Peer(), TestItems.Movie("x", id: "abc"), null, CancellationToken.None);

        handler.Requests.Count.ShouldBe(3);
        foreach (var r in handler.Requests)
        {
            r.Headers.GetValues("X-JellyShare-Token").Single().ShouldBe(Token);
            r.RequestUri!.ToString().ShouldNotContain(Token, Case.Sensitive);
        }
        handler.Requests[1].RequestUri!.ToString().ShouldBe("https://h/jellyfin/JellyShare/v1/image/abc/Primary");
        handler.Requests[2].RequestUri!.ToString().ShouldBe("https://h/jellyfin/JellyShare/v1/stream/abc");
    }

    [Fact]
    public async Task PeerClient_ToleratesOmittedNullFields()
    {
        var (client, _) = Create(new FakeHandler(_ => Json(MinimalCatalog)));
        var result = await client.FetchCatalogAsync(Peer(), null, CancellationToken.None);

        result.Ok.ShouldBeTrue(result.Error);
        var items = result.Catalog!.Items;
        items.Count.ShouldBe(2);
        var movie = items[0];
        movie.ProviderIds.ShouldBeEmpty();
        movie.ImageTypes.ShouldBeEmpty();
        movie.Media.ShouldNotBeNull();
        movie.Media.Audio.ShouldBeEmpty();
        movie.Media.Subtitles.ShouldBeEmpty();
        movie.Year.ShouldBeNull();
        items[1].SeriesId.ShouldBe("s1");
        items[1].Media.BitRate.ShouldBe(1312543);
        result.Catalog.Etag.ShouldBe("abc");
    }

    [Fact]
    public async Task PeerClient_SendsIfNoneMatch_And304IsNotModified()
    {
        var (client, handler) = Create(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotModified)));
        var result = await client.FetchCatalogAsync(Peer(), "abc", CancellationToken.None);

        result.NotModified.ShouldBeTrue();
        result.Catalog.ShouldBeNull();
        handler.Requests.Single().Headers.IfNoneMatch.Single().ToString().ShouldBe("\"abc\"");
    }

    [Fact]
    public async Task PeerClient_Non2xx_IsErrorWithoutToken()
    {
        var (client, _) = Create(new FakeHandler(_ => Json("{\"error\":\"" + Token + "\"}", HttpStatusCode.Unauthorized)));
        var result = await client.FetchCatalogAsync(Peer(), null, CancellationToken.None);
        result.Ok.ShouldBeFalse();
        result.Error.ShouldBe("HTTP 401");
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
        result.Ok.ShouldBeFalse();
        result.Error.ShouldBe(expected);
    }

    [Fact]
    public async Task PeerClient_MinorVersionBump_IsAccepted()
    {
        var body = MinimalCatalog.Replace("\"1.0\"", "\"1.7\"", StringComparison.Ordinal);
        var (client, _) = Create(new FakeHandler(_ => Json(body)));
        ((await client.FetchCatalogAsync(Peer(), null, CancellationToken.None)).Ok).ShouldBeTrue();
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
        result.Ok.ShouldBeFalse();
        result.Error.ShouldBe("timeout");
        PeerClient.DefaultTimeout.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task PeerClient_CatalogHasLongerTimeoutThanImages()
    {
        var handler = new FakeHandler(async (r, ct) =>
        {
            await Task.Delay(300, ct);
            return r.RequestUri!.AbsolutePath.EndsWith("/catalog", StringComparison.Ordinal)
                ? Json(MinimalCatalog)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1 }) };
        });
        var client = new PeerClient(() => new HttpClient(handler, false), TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(5));

        var catalog = await client.FetchCatalogAsync(Peer(), null, CancellationToken.None);
        var image = await client.GetImageAsync(Peer(), "abc", "Primary", CancellationToken.None);

        catalog.Ok.ShouldBeTrue(catalog.Error);
        image.ShouldBeNull();
        PeerClient.CatalogTimeout.ShouldBe(TimeSpan.FromSeconds(120));
        (PeerClient.HttpClientTimeout > PeerClient.CatalogTimeout).ShouldBeTrue();
    }

    [Fact]
    public async Task PeerClient_SendsJellyShareUserAgent()
    {
        var (client, handler) = Create(new FakeHandler(_ => Json(MinimalCatalog)));
        await client.PingAsync("https://h/jellyfin", CancellationToken.None);
        await client.FetchCatalogAsync(Peer(), null, CancellationToken.None);
        await client.GetImageAsync(Peer(), "abc", "Primary", CancellationToken.None);
        using var _ = await client.OpenStreamAsync(Peer(), TestItems.Movie("x", id: "abc"), null, CancellationToken.None);

        var version = typeof(PeerClient).Assembly.GetName().Version!.ToString();
        handler.Requests.Count.ShouldBe(4);
        foreach (var r in handler.Requests)
        {
            r.Headers.UserAgent.ToString().ShouldBe("JellyShare/" + version);
        }
    }

    [Fact]
    public async Task PeerClient_OpenStream_DisposesRequestMessage()
    {
        var (client, handler) = Create(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        using var response = await client.OpenStreamAsync(Peer(), TestItems.Movie("x", id: "abc"), null, CancellationToken.None);

        // A disposed request message refuses new content.
        ShouldExactly.Throw<ObjectDisposedException>(() => handler.Requests.Single().Content = new StringContent("x"));
    }

    [Fact]
    public async Task PeerClient_ConnectionError_DoesNotLeakToken()
    {
        var (client, _) = Create(new FakeHandler(_ => throw new HttpRequestException("refused with " + Token)));
        var result = await client.FetchCatalogAsync(Peer(), null, CancellationToken.None);
        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldNotContain(Token, Case.Sensitive);
        result.Error.ShouldStartWith("connection failed", Case.Sensitive);
    }

    private sealed class DroppingStream : Stream
    {
        private readonly byte[] _head;
        private int _pos;

        public DroppingStream(string head, string token)
        {
            _head = Encoding.UTF8.GetBytes(head);
            SecretInError = token;
        }

        public string SecretInError { get; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => _pos; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= _head.Length)
            {
                throw new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely " + SecretInError);
            }

            var n = Math.Min(count, _head.Length - _pos);
            Array.Copy(_head, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static HttpResponseMessage Dropping(string head)
        => new(HttpStatusCode.OK) { Content = new StreamContent(new DroppingStream(head, Token)) };

    [Fact]
    public async Task PeerClient_MidBodyDrop_IsConnectionError_WithoutToken()
    {
        var (client, _) = Create(new FakeHandler(_ => Dropping("{\"protocolVersion\":\"1.0\",\"items\":[{\"id\":\"m1\",")));

        var result = await client.FetchCatalogAsync(Peer(), null, CancellationToken.None);

        result.Ok.ShouldBeFalse();
        result.Error.ShouldStartWith("connection failed", Case.Sensitive);
        result.Error.ShouldNotContain(Token, Case.Sensitive);
    }

    [Fact]
    public async Task PeerClient_Ping_MidBodyDrop_IsUnreachable()
    {
        var (client, _) = Create(new FakeHandler(_ => Dropping("{\"protocolVersion\":")));

        var result = await client.PingAsync("https://h/jellyfin", CancellationToken.None);

        result.Ok.ShouldBeFalse();
        result.Failure.ShouldBe(PingFailure.Unreachable);
    }

    [Fact]
    public async Task PeerClient_InvalidJson_IsError()
    {
        var (client, _) = Create(new FakeHandler(_ => Json("<html>")));
        var result = await client.FetchCatalogAsync(Peer(), null, CancellationToken.None);
        result.Ok.ShouldBeFalse();
        result.Error.ShouldBe("invalid catalog");
    }

    [Fact]
    public async Task PeerClient_OpenStream_ForwardsRange()
    {
        var (client, handler) = Create(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.PartialContent)));
        using var response = await client.OpenStreamAsync(Peer(), TestItems.Movie("x", id: "abc"), "bytes=100-", CancellationToken.None);
        response.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        handler.Requests.Single().Headers.Range!.ToString().ShouldBe("bytes=100-");
    }

    [Fact]
    public async Task PeerClient_RedirectingFakeHandler_IsErrorAndNotFollowed()
    {
        var (client, handler) = Create(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("https://evil.example/collect") },
        }));
        var result = await client.FetchCatalogAsync(Peer(), null, CancellationToken.None);
        result.Ok.ShouldBeFalse();
        result.Error.ShouldBe("HTTP 302 (redirects are not followed)");
        handler.Requests.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task PeerClient_PrimaryHandler_DoesNotFollowRedirects_SoTheTokenStaysHome()
    {
        using var server = new RedirectServer();
        using var http = new HttpClient(PeerClient.CreatePrimaryHandler());
        var client = new PeerClient(() => http);
        var peer = Peer(server.BaseUrl);

        var result = await client.FetchCatalogAsync(peer, null, CancellationToken.None);
        await client.GetImageAsync(peer, "abc", "Primary", CancellationToken.None);
        using var stream = await client.OpenStreamAsync(peer, TestItems.Movie("x", id: "abc"), null, CancellationToken.None);

        result.Ok.ShouldBeFalse();
        result.Error.ShouldStartWith("HTTP 30", Case.Sensitive);
        stream.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        server.RedirectTargetHits.ShouldBeEmpty();
        server.Hits.ShouldBe(3);
    }

    private sealed class RedirectServer : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();

        public RedirectServer()
        {
            _listener.Start();
            BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _ = Task.Run(LoopAsync, CancellationToken.None);
        }

        public string BaseUrl { get; }

        public int Hits;

        public System.Collections.Concurrent.ConcurrentBag<string> RedirectTargetHits { get; } = new();

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                System.Net.Sockets.TcpClient tcp;
                try
                {
                    tcp = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch (Exception)
                {
                    return;
                }

                using (tcp)
                {
                    var stream = tcp.GetStream();
                    var reader = new StreamReader(stream, Encoding.ASCII);
                    var requestLine = await reader.ReadLineAsync() ?? string.Empty;
                    var line = await reader.ReadLineAsync();
                    while (!string.IsNullOrEmpty(line))
                    {
                        line = await reader.ReadLineAsync();
                    }

                    string response;
                    if (requestLine.Contains("/collect", StringComparison.Ordinal))
                    {
                        RedirectTargetHits.Add(requestLine);
                        response = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}";
                    }
                    else
                    {
                        Interlocked.Increment(ref Hits);
                        response = $"HTTP/1.1 302 Found\r\nLocation: {BaseUrl}/collect\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                    }

                    var bytes = Encoding.ASCII.GetBytes(response);
                    await stream.WriteAsync(bytes, CancellationToken.None);
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }
    }

    [Fact]
    public async Task PeerClient_GetImage_ReturnsBytesOrNull()
    {
        var (client, _) = Create(new FakeHandler(r => r.RequestUri!.AbsolutePath.EndsWith("/Primary", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) }
            : new HttpResponseMessage(HttpStatusCode.NotFound)));

        await using var found = await client.GetImageAsync(Peer(), "abc", "Primary", CancellationToken.None);
        found.ShouldNotBeNull();
        using var ms = new MemoryStream();
        await found.CopyToAsync(ms);
        ms.ToArray().ShouldBe([1, 2, 3]);
        (await client.GetImageAsync(Peer(), "abc", "Backdrop", CancellationToken.None)).ShouldBeNull();
    }
}
