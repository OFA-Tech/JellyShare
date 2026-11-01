using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using JellyShare.Domain.Enums;
using JellyShare.Domain.Interfaces.Peers;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Peers;
using JellyShare.Domain.Models.Protocol;
using JellyShare.Domain.Rules;

namespace JellyShare.Data.Peers.Clients;

/// <summary>HTTP client for the <c>/JellyShare/v1</c> provider API. The token is only ever sent as a header.</summary>
public sealed class PeerClient : IPeerClient
{
    /// <summary>Timeout for image requests (and the cap for pings).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Timeout for catalog requests: a large catalog takes a while to build and transfer.</summary>
    public static readonly TimeSpan CatalogTimeout = TimeSpan.FromSeconds(120);

    /// <summary>The <see cref="HttpClient.Timeout"/> for peer clients; above every per-request timeout used here.</summary>
    public static readonly TimeSpan HttpClientTimeout = TimeSpan.FromSeconds(150);

    private static readonly ProductInfoHeaderValue _userAgent = new(
        "JellyShare",
        typeof(PeerClient).Assembly.GetName().Version?.ToString() ?? "0");

    private const long MaxImageBytes = 32L * 1024 * 1024;
    private const long MaxPingBytes = 64 * 1024;
    private const int MaxServerNameLength = 100;

    private static readonly TimeSpan _pingTimeout = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private static readonly MediaInfo _emptyMedia = new(null, null, null, null, null, [], []);

    private readonly Func<HttpClient> _client;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _catalogTimeout;

    /// <summary>Initializes a new instance of the <see cref="PeerClient"/> class.</summary>
    /// <param name="client">Returns the HTTP client to use (for example from <c>IHttpClientFactory</c>).</param>
    /// <param name="timeout">Timeout for image requests and pings; defaults to <see cref="DefaultTimeout"/>.</param>
    /// <param name="catalogTimeout">
    /// Timeout for catalog requests; defaults to <see cref="CatalogTimeout"/>, or to <paramref name="timeout"/> when only that is given.
    /// </param>
    public PeerClient(Func<HttpClient> client, TimeSpan? timeout = null, TimeSpan? catalogTimeout = null)
    {
        _client = client;
        _timeout = timeout ?? DefaultTimeout;
        _catalogTimeout = catalogTimeout ?? timeout ?? CatalogTimeout;
    }

    /// <summary>Name of the HTTP client registered for peer requests (see <see cref="CreatePrimaryHandler"/>).</summary>
    public const string HttpClientName = "JellyShare.Peer";

    /// <summary>Creates the primary HTTP handler for peer requests: redirects are never followed.</summary>
    /// <returns>The handler.</returns>
    public static HttpMessageHandler CreatePrimaryHandler() => new SocketsHttpHandler
    {
        // The token is a custom header, which HttpClient would forward to a redirect target. Never follow.
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
    };

    /// <summary>Joins a peer base URL (which may have a sub-path) and an API path.</summary>
    /// <param name="baseUrl">The peer base URL, e.g. <c>https://h/jellyfin</c>.</param>
    /// <param name="relative">The API path, e.g. <c>JellyShare/v1/catalog</c>.</param>
    /// <returns>The absolute URI, or <c>null</c> when the base URL is not an absolute http(s) URL.</returns>
    public static Uri? BuildUri(string? baseUrl, string relative) => PeerUri.Build(baseUrl, relative);

    /// <inheritdoc />
    public async Task<PeerPingResult> PingAsync(string baseUrl, CancellationToken ct)
    {
        var uri = BuildUri(baseUrl, "JellyShare/v1/ping");
        if (uri is null)
        {
            return PingFail(PingFailure.InvalidUrl, null);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout < _pingTimeout ? _timeout : _pingTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.Add(_userAgent);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _client().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            var code = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
            if (code[0] == '3')
            {
                return PingFail(PingFailure.Redirect, "HTTP " + code);
            }

            if (!response.IsSuccessStatusCode)
            {
                return PingFail(PingFailure.NotJellyShare, "HTTP " + code);
            }

            var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var limited = new MemoryStream();
                var chunk = new byte[4096];
                int read;
                while (limited.Length < MaxPingBytes && (read = await stream.ReadAsync(chunk, cts.Token).ConfigureAwait(false)) > 0)
                {
                    await limited.WriteAsync(chunk.AsMemory(0, read), cts.Token).ConfigureAwait(false);
                }

                limited.Position = 0;
                using var doc = await JsonDocument.ParseAsync(limited, cancellationToken: cts.Token).ConfigureAwait(false);
                if (doc.RootElement.ValueKind != JsonValueKind.Object
                    || !TryGetString(doc.RootElement, "protocolVersion", out var version))
                {
                    return PingFail(PingFailure.NotJellyShare, "HTTP " + code);
                }

                TryGetString(doc.RootElement, "serverName", out var rawName);
                var name = new string(rawName.Where(c => !char.IsControl(c)).Take(MaxServerNameLength).ToArray());
                return IsCompatible(version)
                    ? new PeerPingResult(true, name, version, PingFailure.None, null)
                    : new PeerPingResult(false, name, version, PingFailure.IncompatibleProtocol, null);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return PingFail(PingFailure.Timeout, null);
        }
        catch (JsonException)
        {
            return PingFail(PingFailure.NotJellyShare, "not JSON");
        }
        catch (HttpRequestException ex)
        {
            return PingFail(PingFailure.Unreachable, ex.Message);
        }
        catch (IOException ex)
        {
            // HttpIOException and other mid-body connection drops.
            return PingFail(PingFailure.Unreachable, ex.Message);
        }
    }

    private static bool TryGetString(JsonElement obj, string name, out string value)
    {
        value = string.Empty;
        foreach (var property in obj.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
            {
                value = property.Value.GetString() ?? string.Empty;
                return true;
            }
        }

        return false;
    }

    private static PeerPingResult PingFail(PingFailure failure, string? detail) => new(false, null, null, failure, detail);

    /// <inheritdoc />
    public async Task<PeerFetchResult> FetchCatalogAsync(RemotePeer peer, string? etag, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(peer);
        var uri = BuildUri(peer.BaseUrl, "JellyShare/v1/catalog");
        if (uri is null)
        {
            return Fail("invalid base url");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_catalogTimeout);
        try
        {
            using var request = CreateRequest(peer, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrEmpty(etag))
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", "\"" + etag.Trim('"') + "\"");
            }

            using var response = await _client().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return new PeerFetchResult(false, true, null, null);
            }

            var code = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
            if (code[0] == '3')
            {
                return Fail("HTTP " + code + " (redirects are not followed)");
            }

            if (!response.IsSuccessStatusCode)
            {
                return Fail("HTTP " + code);
            }

            var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var catalog = await JsonSerializer.DeserializeAsync<CatalogResponse>(stream, _jsonOptions, cts.Token).ConfigureAwait(false);
                if (catalog is null)
                {
                    return Fail("invalid catalog");
                }

                if (!IsCompatible(catalog.ProtocolVersion))
                {
                    return Fail("incompatible protocol " + Shorten(catalog.ProtocolVersion));
                }

                return new PeerFetchResult(true, false, Sanitize(catalog), null);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail("timeout");
        }
        catch (JsonException)
        {
            return Fail("invalid catalog");
        }
        catch (HttpRequestException ex)
        {
            return Fail("connection failed: " + Redact(ex.Message, peer.Token));
        }
        catch (IOException ex)
        {
            // HttpIOException (an IOException, not an HttpRequestException) when the connection drops mid-body.
            return Fail("connection failed: " + Redact(ex.Message, peer.Token));
        }
    }

    /// <inheritdoc />
    public async Task<Stream?> GetImageAsync(RemotePeer peer, string itemId, string type, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(peer);
        var uri = BuildUri(peer.BaseUrl, "JellyShare/v1/image/" + Uri.EscapeDataString(itemId) + "/" + Uri.EscapeDataString(type));
        if (uri is null)
        {
            return null;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        try
        {
            using var request = CreateRequest(peer, uri);
            using var response = await _client().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxImageBytes)
            {
                return null;
            }

            var buffer = new MemoryStream();
            var body = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                var chunk = new byte[81920];
                int read;
                while ((read = await body.ReadAsync(chunk, cts.Token).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > MaxImageBytes)
                    {
                        return null;
                    }

                    await buffer.WriteAsync(chunk.AsMemory(0, read), cts.Token).ConfigureAwait(false);
                }
            }

            buffer.Position = 0;
            return buffer.Length == 0 ? null : buffer;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<HttpResponseMessage> OpenStreamAsync(RemotePeer peer, string itemId, string? range, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(peer);
        var uri = BuildUri(peer.BaseUrl, "JellyShare/v1/stream/" + Uri.EscapeDataString(itemId))
            ?? throw new InvalidOperationException("invalid base url");
        // A GET has no content, so the request message can be disposed as soon as the headers arrived; the
        // response (and its body stream) stays valid until the caller disposes it.
        using var request = CreateRequest(peer, uri);
        if (!string.IsNullOrWhiteSpace(range))
        {
            request.Headers.TryAddWithoutValidation("Range", range);
        }

        try
        {
            return await _client().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new HttpRequestException("connection failed: " + Redact(ex.Message, peer.Token), null, ex.StatusCode);
        }
    }

    private static HttpRequestMessage CreateRequest(RemotePeer peer, Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.Add(_userAgent);
        request.Headers.TryAddWithoutValidation(ProtocolInfo.TokenHeader, peer.Token ?? string.Empty);
        return request;
    }

    private static bool IsCompatible(string? version)
    {
        var major = (version ?? string.Empty).Split('.')[0];
        return int.TryParse(major, NumberStyles.None, CultureInfo.InvariantCulture, out var m) && m == 1;
    }

    private static string Shorten(string? value)
    {
        var v = string.IsNullOrWhiteSpace(value) ? "(none)" : value.Trim();
        v = new string(v.Where(c => !char.IsControl(c)).ToArray());
        return v.Length > 16 ? v[..16] : v;
    }

    private static string Redact(string message, string? token)
        => string.IsNullOrEmpty(token) ? message : message.Replace(token, "***", StringComparison.Ordinal);

    private static PeerFetchResult Fail(string error) => new(false, false, null, error);

    private static CatalogResponse Sanitize(CatalogResponse catalog)
    {
        var items = (catalog.Items ?? [])
            .Where(i => i is not null
                && !string.IsNullOrWhiteSpace(i.Id)
                && (i.Type == "Movie" || i.Type == "Episode"))
            .Select(i => i with
            {
                Name = i.Name ?? string.Empty,
                ProviderIds = i.ProviderIds ?? new Dictionary<string, string>(),
                Media = i.Media is null
                    ? _emptyMedia
                    : i.Media with
                    {
                        Audio = (i.Media.Audio ?? []).Where(a => a is not null).ToArray(),
                        Subtitles = (i.Media.Subtitles ?? []).Where(s => s is not null).ToArray(),
                    },
                ImageTypes = (i.ImageTypes ?? []).Where(t => t is not null).ToArray(),
            })
            .ToList();
        return catalog with
        {
            ServerName = catalog.ServerName ?? string.Empty,
            Etag = catalog.Etag ?? string.Empty,
            Items = items,
        };
    }
}
