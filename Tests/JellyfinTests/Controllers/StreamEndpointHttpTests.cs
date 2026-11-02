using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using JellyShare.Application.Features.Catalog;
using JellyShare.Application.Setup;
using JellyShare.Application.Features.Streaming;
using JellyShare.Domain.Interfaces.Catalog;
using JellyShare.Domain.Interfaces.Configuration;
using JellyShare.Domain.Interfaces.Library;
using JellyShare.Domain.Interfaces.Platform;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Library;
using JellyShare.Jellyfin.Controllers.V1;
using JellyShare.UnitTests.Fakes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JellyShare.JellyfinTests.Controllers;

/// <summary>Runs the controller in a real Kestrel host to prove the routes, Range handling and HEAD.</summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "xUnit disposes the host and client through IAsyncLifetime.DisposeAsync.")]
public sealed class StreamEndpointHttpTests : IAsyncLifetime
{
    private static readonly Guid _lib = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid _movieId = Guid.Parse("00000001-0000-0000-0000-000000000001");
    private static readonly Guid _cappedId = Guid.Parse("00000001-0000-0000-0000-000000000002");

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "jellyshare-test-" + Guid.NewGuid().ToString("N"));
    private readonly FakeConfig _config = new();
    private readonly ShareTokenService _tokens = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _token = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        var file = Path.Combine(_tempDir, "a.mkv");
        await File.WriteAllBytesAsync(file, Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray());
        var (token, hash) = _tokens.Create();
        _token = token;
        _config.Settings.Shares.Add(new PeerShare { Id = Guid.NewGuid(), Name = "bob", TokenHash = hash, LibraryIds = new List<Guid> { _lib } });

        var source = new FakeSource();
        source.Items.Add((_lib, new LocalItem(_movieId, "Movie", file, null, () => throw new InvalidOperationException())));
        source.Streams[_movieId] = new StreamInfo("Movie", "Movie", file, 1_000_000);
        source.Items.Add((_lib, new LocalItem(_cappedId, "Movie", file, null, () => throw new InvalidOperationException())));
        source.Streams[_cappedId] = new StreamInfo("Movie", "Capped", file, 50_000_000);
        _config.Settings.Shares[0].MaxBitrate = 2_000_000;
        _config.Settings.Shares[0].MaxStreams = 1;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddControllers().AddApplicationPart(typeof(FederationController).Assembly);
        builder.Services.AddSingleton<IServerIdentity>(new FakeIdentity());
        builder.Services.AddSingleton<ISettingsProvider>(_config);
        builder.Services.AddSingleton<IShareTokenService>(_tokens);
        builder.Services.AddSingleton(new StreamLimiter(TimeProvider.System));
        builder.Services.AddSingleton<ILocalItemSource>(source);
        builder.Services.AddSingleton(FakeCapped.Create());
        builder.Services.AddSingleton(sp => new CatalogBuilder(source, new FakeIdentity(), _config, _ => true));
        builder.Services.AddDispatcherSetup();
        _app = builder.Build();
        _app.MapControllers();
        await _app.StartAsync();
        var address = _app.Urls.First();
        _client = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        Directory.Delete(_tempDir, true);
    }

    private static HttpRequestMessage Request(HttpMethod method, string? token, Action<HttpRequestMessage>? tweak = null)
    {
        var request = new HttpRequestMessage(method, "/JellyShare/v1/stream/" + _movieId.ToString("N"));
        if (token is not null)
        {
            request.Headers.Add("X-JellyShare-Token", token);
        }

        tweak?.Invoke(request);
        return request;
    }

    [Fact]
    public async Task Get_WithRange_Returns206With1024Bytes()
    {
        var response = await _client.SendAsync(Request(HttpMethod.Get, _token, r => r.Headers.Range = new RangeHeaderValue(0, 1023)));

        response.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        response.Content.Headers.ContentRange!.ToString().ShouldBe("bytes 0-1023/4096");
        response.Content.Headers.ContentType!.MediaType.ShouldBe("video/x-matroska");
        ((await response.Content.ReadAsByteArrayAsync()).Length).ShouldBe(1024);
    }

    [Fact]
    public async Task Get_OpenEndedRange_ReturnsTheRest()
    {
        var response = await _client.SendAsync(Request(HttpMethod.Get, _token, r => r.Headers.Range = new RangeHeaderValue(4000, null)));

        response.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        ((await response.Content.ReadAsByteArrayAsync()).Length).ShouldBe(96);
    }

    [Fact]
    public async Task Head_ReturnsLengthWithoutBody()
    {
        var response = await _client.SendAsync(Request(HttpMethod.Head, _token));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentLength.ShouldBe(4096);
        (await response.Content.ReadAsByteArrayAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Capped_GetWithRange_Returns200TransportStream_WithoutRanges()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/JellyShare/v1/stream/" + _cappedId.ToString("N") + "?start=10");
        request.Headers.Add("X-JellyShare-Token", _token);
        request.Headers.Range = new RangeHeaderValue(100, null);

        var response = await _client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("video/mp2t");
        string.Join(",", response.Headers.AcceptRanges).ShouldBe("none");
        response.Content.Headers.ContentRange.ShouldBeNull();
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe(FakeFfmpegRunner.Payload);
    }

    [Fact]
    public async Task Capped_InvalidStart_Returns400()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/JellyShare/v1/stream/" + _cappedId.ToString("N") + "?start=-3");
        request.Headers.Add("X-JellyShare-Token", _token);

        ((await _client.SendAsync(request)).StatusCode).ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task NoToken_Returns401()
    {
        var response = await _client.SendAsync(Request(HttpMethod.Get, null));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
