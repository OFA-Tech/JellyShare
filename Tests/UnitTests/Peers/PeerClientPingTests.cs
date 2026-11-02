using System.Net;
using System.Text;
using JellyShare.Data.Peers.Clients;
using JellyShare.Domain.Enums;
using JellyShare.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace JellyShare.UnitTests.Peers;

public sealed class PeerClientPingTests
{
    private static HttpResponseMessage Resp(HttpStatusCode code, string body, string type = "application/json")
        => new(code) { Content = new StringContent(body, Encoding.UTF8, type) };

    private static PeerClient Client(FakeHandler handler, TimeSpan? timeout = null)
        => new(() => new HttpClient(handler, false), timeout);

    [Fact]
    public async Task Ping_Ok_UsesPingUrlWithSubPath_AndSendsNoToken()
    {
        var handler = new FakeHandler(_ => Resp(HttpStatusCode.OK, """{"protocolVersion":"1.0","serverName":"Alice"}"""));

        var result = await Client(handler).PingAsync(" http://h/jellyfin/ ", CancellationToken.None);

        result.Ok.ShouldBeTrue();
        result.ServerName.ShouldBe("Alice");
        result.ProtocolVersion.ShouldBe("1.0");
        var request = handler.Requests.ShouldHaveSingleItem();
        request.RequestUri!.ToString().ShouldBe("http://h/jellyfin/JellyShare/v1/ping");
        request.Headers.Contains("X-JellyShare-Token").ShouldBeFalse();
    }

    [Theory]
    [InlineData("ftp://h")]
    [InlineData("not a url")]
    [InlineData("")]
    public async Task Ping_InvalidUrl(string url)
    {
        var handler = new FakeHandler(_ => Resp(HttpStatusCode.OK, "{}"));

        var result = await Client(handler).PingAsync(url, CancellationToken.None);

        result.Failure.ShouldBe(PingFailure.InvalidUrl);
        handler.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "<html>nope</html>", "text/html")]
    [InlineData(HttpStatusCode.OK, "<!doctype html><html></html>", "text/html")]
    [InlineData(HttpStatusCode.OK, """{"hello":"world"}""", "application/json")]
    [InlineData(HttpStatusCode.OK, "[1,2]", "application/json")]
    [InlineData(HttpStatusCode.InternalServerError, "", "text/plain")]
    [InlineData(HttpStatusCode.Unauthorized, "", "text/plain")]
    public async Task Ping_NotAJellyShareAnswer_IsNotJellyShare(HttpStatusCode code, string body, string type)
    {
        var result = await Client(new FakeHandler(_ => Resp(code, body, type))).PingAsync("http://h", CancellationToken.None);

        result.Ok.ShouldBeFalse();
        result.Failure.ShouldBe(PingFailure.NotJellyShare);
    }

    [Fact]
    public async Task Ping_Redirect_IsRefused()
    {
        var handler = new FakeHandler(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Found);
            r.Headers.Location = new Uri("https://elsewhere/");
            return r;
        });

        var result = await Client(handler).PingAsync("http://h", CancellationToken.None);

        result.Failure.ShouldBe(PingFailure.Redirect);
    }

    [Theory]
    [InlineData("2.0")]
    [InlineData("0.9")]
    [InlineData("")]
    public async Task Ping_OtherMajorVersion_IsIncompatible(string version)
    {
        var body = $$"""{"protocolVersion":"{{version}}","serverName":"X"}""";

        var result = await Client(new FakeHandler(_ => Resp(HttpStatusCode.OK, body))).PingAsync("http://h", CancellationToken.None);

        result.Ok.ShouldBeFalse();
        result.Failure.ShouldBe(PingFailure.IncompatibleProtocol);
    }

    [Fact]
    public async Task Ping_ServerName_IsTruncatedTo100Chars_AndControlCharactersRemoved()
    {
        var name = "A\nB\u0007" + new string('x', 300);
        var body = System.Text.Json.JsonSerializer.Serialize(new { protocolVersion = "1.0", serverName = name });

        var result = await Client(new FakeHandler(_ => Resp(HttpStatusCode.OK, body))).PingAsync("http://h", CancellationToken.None);

        result.Ok.ShouldBeTrue();
        result.ServerName!.Length.ShouldBe(100);
        result.ServerName.ShouldStartWith("ABxxx", Case.Sensitive);
        result.ServerName.ShouldNotContain(c => char.IsControl(c));
    }

    [Fact]
    public async Task Ping_ConnectionError_IsUnreachable()
    {
        var handler = new FakeHandler((_, _) => throw new HttpRequestException("No connection could be made"));

        var result = await Client(handler).PingAsync("http://h", CancellationToken.None);

        result.Failure.ShouldBe(PingFailure.Unreachable);
    }

    [Fact]
    public async Task Ping_Slow_IsTimeout()
    {
        var handler = new FakeHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return Resp(HttpStatusCode.OK, "{}");
        });

        var result = await Client(handler, TimeSpan.FromMilliseconds(100)).PingAsync("http://h", CancellationToken.None);

        result.Failure.ShouldBe(PingFailure.Timeout);
    }
}
