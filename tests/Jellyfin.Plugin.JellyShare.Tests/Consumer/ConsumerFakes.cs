using System.Net;
using Jellyfin.Plugin.JellyShare.Configuration;
using Jellyfin.Plugin.JellyShare.Consumer;
using Jellyfin.Plugin.JellyShare.Placeholders;
using Jellyfin.Plugin.JellyShare.Protocol;
using Jellyfin.Plugin.JellyShare.Provider;

namespace Jellyfin.Plugin.JellyShare.Tests.Consumer;

internal sealed class MutableClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jellyshare-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Sub(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch (IOException)
        {
        }
    }
}

internal sealed class ConsumerConfig : IConfigProvider
{
    public PluginConfiguration Current { get; } = new();
}

internal sealed class FakePeerClient : IPeerClient
{
    public Dictionary<Guid, Func<string?, PeerFetchResult>> Catalogs { get; } = new();

    public Dictionary<(Guid Peer, string Item, string Type), byte[]> Images { get; } = new();

    public List<(Guid Peer, string? Etag)> Fetches { get; } = new();

    public List<(Guid Peer, string Item, string Type)> ImageRequests { get; } = new();

    public Func<Task>? BeforeFetch { get; set; }

    public int Entered => _entered;

    private int _entered;

    public Func<string, PeerPingResult> Pings { get; set; } = _ => new PeerPingResult(true, "peer", ProtocolInfo.Version, PingFailure.None, null);

    public List<string> PingedUrls { get; } = new();

    public Task<PeerPingResult> PingAsync(string baseUrl, CancellationToken ct)
    {
        PingedUrls.Add(baseUrl);
        return Task.FromResult(Pings(baseUrl));
    }

    public async Task<PeerFetchResult> FetchCatalogAsync(RemotePeer peer, string? etag, CancellationToken ct)
    {
        Interlocked.Increment(ref _entered);
        if (BeforeFetch is not null)
        {
            await BeforeFetch();
        }

        lock (Fetches)
        {
            Fetches.Add((peer.Id, etag));
        }

        return Catalogs.TryGetValue(peer.Id, out var f)
            ? f(etag)
            : new PeerFetchResult(false, false, null, "HTTP 404");
    }

    public Task<Stream?> GetImageAsync(RemotePeer peer, string itemId, string type, CancellationToken ct)
    {
        ImageRequests.Add((peer.Id, itemId, type));
        return Task.FromResult<Stream?>(Images.TryGetValue((peer.Id, itemId, type), out var b) ? new MemoryStream(b) : null);
    }

    public Task<HttpResponseMessage> OpenStreamAsync(RemotePeer peer, string itemId, string? range, CancellationToken ct)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

    public static PeerFetchResult Ok(params CatalogItem[] items)
        => new(true, false, new CatalogResponse(ProtocolInfo.Version, "peer", "etag-" + items.Length, items), null);

    public static PeerFetchResult Fail(string error) => new(false, false, null, error);
}

internal sealed class FakeLocalLibrary : ILocalLibrary
{
    public List<LocalMedia> Media { get; } = new();

    public List<LocalSeriesItem> Series { get; } = new();

    public List<string> Locations { get; } = new();

    public int Reads { get; private set; }

    public LocalLibrarySnapshot Read()
    {
        Reads++;
        return new(Media.ToList(), Series.ToList()) { Locations = Locations.ToList() };
    }

    public int ScopedReads { get; private set; }

    public LibraryScope? LastScope { get; private set; }

    public LocalLibrarySnapshot Read(LibraryScope scope)
    {
        ScopedReads++;
        LastScope = scope;

        // Like the provider-id query: movies by their ids, series by theirs, and the episodes of those series.
        static bool Matches(IReadOnlyDictionary<string, string>? ids, IReadOnlyDictionary<string, IReadOnlySet<string>> wanted)
            => ids is not null && ids.Any(kv => wanted.TryGetValue(kv.Key.ToLowerInvariant(), out var set) && set.Contains(kv.Value.ToLowerInvariant()));

        var media = Media.Where(m => m.Type == "Movie"
                ? Matches(m.ProviderIds, scope.MovieIds)
                : Matches(m.SeriesProviderIds, scope.SeriesIds))
            .ToList();
        var series = Series.Where(s => Matches(s.ProviderIds, scope.SeriesIds)).ToList();
        return new(media, series);
    }

    public IReadOnlyList<string> GetLocations() => Locations.ToList();

    public static Func<string, FilePresence> Exists(Func<string, bool> exists)
        => p => exists(p) ? FilePresence.Present : FilePresence.Missing;
}

internal sealed class FakeScans : IScanReporter
{
    public List<string> Reported { get; } = new();

    public Func<string, bool>? FailFor { get; set; }

    public void ReportChanged(string fullPath)
    {
        if (FailFor?.Invoke(fullPath) == true)
        {
            throw new InvalidOperationException("monitor unavailable");
        }

        Reported.Add(fullPath);
    }
}

internal sealed class FixedRelay : IRelayBase
{
    public string Current => "http://127.0.0.1:8096";
}

internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

    public FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        _respond = respond;
    }

    public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : this((r, _) => Task.FromResult(respond(r)))
    {
    }

    public List<HttpRequestMessage> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return _respond(request, cancellationToken);
    }
}
