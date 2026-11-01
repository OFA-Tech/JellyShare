using System.Net;
using System.Net.Http.Headers;
using Jellyfin.Plugin.JellyShare.Configuration;
using Jellyfin.Plugin.JellyShare.Provider;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.JellyShare.Tests.Provider;

/// <summary>Runs the controller in a real Kestrel host to prove the routes, Range handling and HEAD.</summary>
public sealed class StreamEndpointHttpTests : IAsyncLifetime
{
    private static readonly Guid Lib = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid MovieId = Guid.Parse("00000001-0000-0000-0000-000000000001");

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
        File.WriteAllBytes(file, Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray());
        var (token, hash) = _tokens.Create();
        _token = token;
        _config.Current.Shares.Add(new PeerShare { Id = Guid.NewGuid(), Name = "bob", TokenHash = hash, LibraryIds = new List<Guid> { Lib } });

        var source = new FakeSource();
        source.Items.Add((Lib, new LocalItem(MovieId, "Movie", file, null, () => throw new InvalidOperationException())));
        source.Streams[MovieId] = new StreamInfo("Movie", "Movie", file, null);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddControllers().AddApplicationPart(typeof(FederationController).Assembly);
        builder.Services.AddSingleton<IServerIdentity>(new FakeIdentity());
        builder.Services.AddSingleton<IConfigProvider>(_config);
        builder.Services.AddSingleton(_tokens);
        builder.Services.AddSingleton(new StreamLimiter(TimeProvider.System));
        builder.Services.AddSingleton<ILocalItemSource>(source);
        builder.Services.AddSingleton(sp => new CatalogBuilder(source, new FakeIdentity(), _config, _ => true));
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

    private HttpRequestMessage Request(HttpMethod method, string? token, Action<HttpRequestMessage>? tweak = null)
    {
        var request = new HttpRequestMessage(method, "/JellyShare/v1/stream/" + MovieId.ToString("N"));
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

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bytes 0-1023/4096", response.Content.Headers.ContentRange!.ToString());
        Assert.Equal("video/x-matroska", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(1024, (await response.Content.ReadAsByteArrayAsync()).Length);
    }

    [Fact]
    public async Task Get_OpenEndedRange_ReturnsTheRest()
    {
        var response = await _client.SendAsync(Request(HttpMethod.Get, _token, r => r.Headers.Range = new RangeHeaderValue(4000, null)));

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(96, (await response.Content.ReadAsByteArrayAsync()).Length);
    }

    [Fact]
    public async Task Head_ReturnsLengthWithoutBody()
    {
        var response = await _client.SendAsync(Request(HttpMethod.Head, _token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(4096, response.Content.Headers.ContentLength);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task NoToken_Returns401()
    {
        var response = await _client.SendAsync(Request(HttpMethod.Get, null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
