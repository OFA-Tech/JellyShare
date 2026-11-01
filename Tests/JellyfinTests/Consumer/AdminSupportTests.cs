using System.Net;
using System.Text;
using JellyShare.Jellyfin.Configuration;
using JellyShare.Jellyfin.Consumer;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JellyShare.JellyfinTests.Consumer;

public sealed class SyncReportStoreTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void SaveThenLoad_RoundTripsAllFields()
    {
        var report = new SyncReport(10, 2, 3, 4, 5, ["bob: HTTP 401"])
        {
            Skipped = 1,
            Conflicts = 2,
            Scanned = 3,
            PendingScans = 4,
            ImagesWritten = 5,
            UncheckedLocalFiles = 6,
            MissingLocalFiles = 7,
            FinishedUtc = new DateTimeOffset(2026, 10, 31, 12, 30, 0, TimeSpan.Zero),
        };

        SyncReportStore.Save(_tmp.Sub("data"), report);
        var loaded = SyncReportStore.Load(_tmp.Sub("data"));

        Assert.NotNull(loaded);
        Assert.Equal(report.Offered, loaded!.Offered);
        Assert.Equal(report.Unmatched, loaded.Unmatched);
        Assert.Equal(report.SkippedLocal, loaded.SkippedLocal);
        Assert.Equal(report.Written, loaded.Written);
        Assert.Equal(report.Deleted, loaded.Deleted);
        Assert.Equal(report.PeerErrors, loaded.PeerErrors);
        Assert.Equal(report.Skipped, loaded.Skipped);
        Assert.Equal(report.Conflicts, loaded.Conflicts);
        Assert.Equal(report.Scanned, loaded.Scanned);
        Assert.Equal(report.PendingScans, loaded.PendingScans);
        Assert.Equal(report.ImagesWritten, loaded.ImagesWritten);
        Assert.Equal(report.UncheckedLocalFiles, loaded.UncheckedLocalFiles);
        Assert.Equal(report.MissingLocalFiles, loaded.MissingLocalFiles);
        Assert.Equal(report.FinishedUtc, loaded.FinishedUtc);
    }

    [Fact]
    public void Load_MissingOrCorruptFile_IsNull()
    {
        Assert.Null(SyncReportStore.Load(_tmp.Sub("data")));

        Directory.CreateDirectory(_tmp.Sub("data"));
        File.WriteAllText(Path.Combine(_tmp.Sub("data"), SyncReportStore.FileName), "{ not json");

        Assert.Null(SyncReportStore.Load(_tmp.Sub("data")));
    }

    [Fact]
    public async Task SyncService_PersistsLastReport_SoANewInstanceSeesItAfterRestart()
    {
        var config = new ConsumerConfig();
        config.Current.MoviesPlaceholderPath = _tmp.Sub("ph", "Movies");
        config.Current.ShowsPlaceholderPath = _tmp.Sub("ph", "Shows");
        SyncService Create() => new(
            config,
            new FakePeerClient(),
            new LocalIndex(new FakeLocalLibrary(), config, FakeLocalLibrary.Exists(_ => true)),
            new FakeScans(),
            new FixedRelay(),
            () => _tmp.Sub("data"),
            new MutableClock(),
            NullLogger<SyncService>.Instance);

        var first = Create();
        Assert.Null(first.LastReport);
        var report = await first.RunAsync(CancellationToken.None);

        var restarted = Create();

        Assert.NotNull(restarted.LastReport);
        Assert.Equal(report.Offered, restarted.LastReport!.Offered);
        Assert.Equal(report.FinishedUtc, restarted.LastReport.FinishedUtc);
    }
}

public class PeerClientPingTests
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

        Assert.True(result.Ok);
        Assert.Equal("Alice", result.ServerName);
        Assert.Equal("1.0", result.ProtocolVersion);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://h/jellyfin/JellyShare/v1/ping", request.RequestUri!.ToString());
        Assert.False(request.Headers.Contains("X-JellyShare-Token"));
    }

    [Theory]
    [InlineData("ftp://h")]
    [InlineData("not a url")]
    [InlineData("")]
    public async Task Ping_InvalidUrl(string url)
    {
        var handler = new FakeHandler(_ => Resp(HttpStatusCode.OK, "{}"));

        var result = await Client(handler).PingAsync(url, CancellationToken.None);

        Assert.Equal(PingFailure.InvalidUrl, result.Failure);
        Assert.Empty(handler.Requests);
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

        Assert.False(result.Ok);
        Assert.Equal(PingFailure.NotJellyShare, result.Failure);
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

        Assert.Equal(PingFailure.Redirect, result.Failure);
    }

    [Theory]
    [InlineData("2.0")]
    [InlineData("0.9")]
    [InlineData("")]
    public async Task Ping_OtherMajorVersion_IsIncompatible(string version)
    {
        var body = $$"""{"protocolVersion":"{{version}}","serverName":"X"}""";

        var result = await Client(new FakeHandler(_ => Resp(HttpStatusCode.OK, body))).PingAsync("http://h", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(PingFailure.IncompatibleProtocol, result.Failure);
    }

    [Fact]
    public async Task Ping_ServerName_IsTruncatedTo100Chars_AndControlCharactersRemoved()
    {
        var name = "A\nB\u0007" + new string('x', 300);
        var body = System.Text.Json.JsonSerializer.Serialize(new { protocolVersion = "1.0", serverName = name });

        var result = await Client(new FakeHandler(_ => Resp(HttpStatusCode.OK, body))).PingAsync("http://h", CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(100, result.ServerName!.Length);
        Assert.StartsWith("ABxxx", result.ServerName, StringComparison.Ordinal);
        Assert.DoesNotContain(result.ServerName, c => char.IsControl(c));
    }

    [Fact]
    public async Task Ping_ConnectionError_IsUnreachable()
    {
        var handler = new FakeHandler((_, _) => throw new HttpRequestException("No connection could be made"));

        var result = await Client(handler).PingAsync("http://h", CancellationToken.None);

        Assert.Equal(PingFailure.Unreachable, result.Failure);
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

        Assert.Equal(PingFailure.Timeout, result.Failure);
    }
}
