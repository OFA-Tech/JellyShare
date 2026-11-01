using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.JellyShare.Configuration;
using Jellyfin.Plugin.JellyShare.Protocol;

namespace Jellyfin.Plugin.JellyShare.Consumer;

/// <summary>Result of a catalog fetch.</summary>
/// <param name="Ok">The catalog was fetched and is compatible.</param>
/// <param name="NotModified">The peer answered 304 for the sent ETag.</param>
/// <param name="Catalog">The catalog when <paramref name="Ok"/>.</param>
/// <param name="Error">A short error that never contains the token.</param>
public sealed record PeerFetchResult(bool Ok, bool NotModified, CatalogResponse? Catalog, string? Error);

/// <summary>Talks to a peer's provider endpoints.</summary>
public interface IPeerClient
{
    /// <summary>Fetches the peer's catalog.</summary>
    /// <param name="peer">The peer.</param>
    /// <param name="etag">The ETag of the last good catalog, if any.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The result.</returns>
    Task<PeerFetchResult> FetchCatalogAsync(RemotePeer peer, string? etag, CancellationToken ct);

    /// <summary>Downloads an image of a peer item.</summary>
    /// <param name="peer">The peer.</param>
    /// <param name="itemId">The peer item (or series) id.</param>
    /// <param name="type">The image type, e.g. <c>Primary</c>.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The image bytes, or <c>null</c> on any failure.</returns>
    Task<Stream?> GetImageAsync(RemotePeer peer, string itemId, string type, CancellationToken ct);

    /// <summary>Opens the peer's stream of an item. The caller disposes the response.</summary>
    /// <param name="peer">The peer.</param>
    /// <param name="itemId">The peer item id.</param>
    /// <param name="range">The <c>Range</c> header to forward, if any.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The response, read up to the headers only.</returns>
    Task<HttpResponseMessage> OpenStreamAsync(RemotePeer peer, string itemId, string? range, CancellationToken ct);
}

/// <summary>HTTP client for the <c>/JellyShare/v1</c> provider API. The token is only ever sent as a header.</summary>
public sealed class PeerClient : IPeerClient
{
    /// <summary>Timeout for catalog and image requests.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private const long MaxImageBytes = 32L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private static readonly MediaInfo EmptyMedia = new(null, null, null, null, null, [], []);

    private readonly Func<HttpClient> _client;
    private readonly TimeSpan _timeout;

    /// <summary>Initializes a new instance of the <see cref="PeerClient"/> class.</summary>
    /// <param name="client">Returns the HTTP client to use (for example from <c>IHttpClientFactory</c>).</param>
    /// <param name="timeout">Timeout for catalog and image requests; defaults to <see cref="DefaultTimeout"/>.</param>
    public PeerClient(Func<HttpClient> client, TimeSpan? timeout = null)
    {
        _client = client;
        _timeout = timeout ?? DefaultTimeout;
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
    public static Uri? BuildUri(string? baseUrl, string relative)
    {
        ArgumentNullException.ThrowIfNull(relative);
        if (string.IsNullOrWhiteSpace(baseUrl)
            || !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var b)
            || (b.Scheme != Uri.UriSchemeHttp && b.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(b.Query)
            || !string.IsNullOrEmpty(b.Fragment))
        {
            return null;
        }

        return new Uri(b.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/" + relative.TrimStart('/'));
    }

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
        cts.CancelAfter(_timeout);
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
                var catalog = await JsonSerializer.DeserializeAsync<CatalogResponse>(stream, JsonOptions, cts.Token).ConfigureAwait(false);
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

                    buffer.Write(chunk, 0, read);
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
        var request = CreateRequest(peer, uri);
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
            request.Dispose();
            throw new HttpRequestException("connection failed: " + Redact(ex.Message, peer.Token), null, ex.StatusCode);
        }
    }

    private static HttpRequestMessage CreateRequest(RemotePeer peer, Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation(Provider.FederationController.TokenHeader, peer.Token ?? string.Empty);
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
                    ? EmptyMedia
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
