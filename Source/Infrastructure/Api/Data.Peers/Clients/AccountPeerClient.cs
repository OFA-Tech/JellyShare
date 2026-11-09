using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using JellyShare.Domain.Enums;
using JellyShare.Domain.Interfaces.Peers;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Peers;
using JellyShare.Domain.Models.Protocol;
using JellyShare.Domain.Rules;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JellyShare.Data.Peers.Clients;

/// <summary>
/// Client for a friend's plain Jellyfin server reached with a user account (an account peer). The password is only sent
/// in the sign-in body; the access token only in the <c>Authorization</c> header.
/// </summary>
public sealed class AccountPeerClient : IPeerClient, IAccountSignIn
{
    private const int PageSize = 500;
    private const int MaxPages = 1000;
    private const int AudioBitRate = 192000;
    private const int MinVideoBitRate = 64000;
    private const int MaxServerNameLength = 100;

    private static readonly TimeSpan _policyLifetime = TimeSpan.FromHours(1);

    private static readonly string _version = typeof(AccountPeerClient).Assembly.GetName().Version?.ToString() ?? "0";

    private static readonly ProductInfoHeaderValue _userAgent = new("JellyShare", _version);

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly Func<HttpClient> _client;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _catalogTimeout;
    private readonly ILogger<AccountPeerClient> _logger;
    // Keyed by peer, valid only for the token it was read with: a sign-in as another user must not reuse the limit.
    private readonly ConcurrentDictionary<Guid, (string Token, int Limit, DateTimeOffset FetchedAt)> _policies = new();

    /// <summary>Initializes a new instance of the <see cref="AccountPeerClient"/> class.</summary>
    /// <param name="client">Returns the HTTP client to use (the redirect-free <see cref="PeerClient.HttpClientName"/> client).</param>
    /// <param name="clock">The clock for the bitrate-policy cache; defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="timeout">Timeout for sign-in, image and policy requests; defaults to <see cref="PeerClient.DefaultTimeout"/>.</param>
    /// <param name="catalogTimeout">Timeout for each catalog page; defaults to <see cref="PeerClient.CatalogTimeout"/>.</param>
    /// <param name="logger">The logger; defaults to none.</param>
    public AccountPeerClient(
        Func<HttpClient> client,
        TimeProvider? clock = null,
        TimeSpan? timeout = null,
        TimeSpan? catalogTimeout = null,
        ILogger<AccountPeerClient>? logger = null)
    {
        _client = client;
        _logger = logger ?? NullLogger<AccountPeerClient>.Instance;
        _clock = clock ?? TimeProvider.System;
        _timeout = timeout ?? PeerClient.DefaultTimeout;
        _catalogTimeout = catalogTimeout ?? timeout ?? PeerClient.CatalogTimeout;
    }

    /// <inheritdoc />
    /// <remarks>Plain Jellyfin servers have no JellyShare ping; <see cref="PeerClientRouter"/> sends pings to the token client.</remarks>
    public Task<PeerPingResult> PingAsync(string baseUrl, CancellationToken ct)
        => Task.FromResult(new PeerPingResult(false, null, null, PingFailure.NotJellyShare, "account peers are not pinged"));

    /// <inheritdoc />
    public async Task<AccountSignInResult> SignInAsync(string baseUrl, string username, string password, Guid peerId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(password);
        var uri = PeerUri.Build(baseUrl, "Users/AuthenticateByName");
        if (uri is null)
        {
            return SignInFail("invalid base url");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        try
        {
            using var request = CreateRequest(HttpMethod.Post, uri, peerId, null);
            request.Content = JsonContent.Create(new Dictionary<string, string> { ["Username"] = username, ["Pw"] = password });
            using var response = await _client().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return SignInFail(AccountPeerInfo.SignInFailedPrefix + ": wrong username or password");
            }

            var error = StatusError(response.StatusCode);
            if (error is not null)
            {
                return SignInFail(error.StartsWith("HTTP 3", StringComparison.Ordinal) ? error : AccountPeerInfo.SignInFailedPrefix + ": " + error);
            }

            var auth = await response.Content.ReadFromJsonAsync<JfAuthResult>(_jsonOptions, cts.Token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(auth?.AccessToken) || string.IsNullOrEmpty(auth.User?.Id))
            {
                return SignInFail(AccountPeerInfo.SignInFailedPrefix + ": invalid response");
            }

            var name = await GetServerNameAsync(baseUrl, peerId, auth.AccessToken, cts.Token).ConfigureAwait(false);
            return new AccountSignInResult(true, auth.AccessToken, auth.User.Id, name, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return SignInFail("timeout");
        }
        catch (JsonException)
        {
            return SignInFail(AccountPeerInfo.SignInFailedPrefix + ": invalid response");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return SignInFail("connection failed: " + PeerClient.Redact(ex.Message, password));
        }
    }

    /// <inheritdoc />
    public async Task SignOutAsync(string baseUrl, string accessToken, Guid peerId, CancellationToken ct)
    {
        var uri = PeerUri.Build(baseUrl, "Sessions/Logout");
        if (uri is null)
        {
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        try
        {
            using var request = CreateRequest(HttpMethod.Post, uri, peerId, accessToken);
            using var response = await _client().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            // Best effort: the session expires on the friend's server anyway.
        }
    }

    /// <inheritdoc />
    public async Task<PeerFetchResult> FetchCatalogAsync(RemotePeer peer, string? etag, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(peer);
        var info = PeerUri.Build(peer.BaseUrl, "System/Info/Public");
        if (info is null)
        {
            return Fail("invalid base url");
        }

        try
        {
            var (publicInfo, error) = await GetJsonAsync<JfPublicInfo>(info, peer.Id, peer.Token, _catalogTimeout, ct).ConfigureAwait(false);
            if (error is not null)
            {
                return Fail(error);
            }

            var requested = MediaTypes.Requested(peer);
            var series = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            if (requested.Contains(MediaType.Shows))
            {
                error = await PageAsync(peer, "Series", "ProviderIds", s => series[s.Id ?? string.Empty] = Ids(s.ProviderIds), ct).ConfigureAwait(false);
                if (error is not null)
                {
                    return Fail(error);
                }
            }

            var shared = new List<JfItem>();
            if (requested.Count > 0)
            {
                error = await PageAsync(
                    peer,
                    string.Join(',', requested.Select(MediaTypes.CatalogItemType)),
                    "ProviderIds,Overview,MediaSources,DateCreated,ParentId&EnableImages=true",
                    i =>
                    {
                        if (IsShared(i, peer))
                        {
                            shared.Add(i);
                        }
                    },
                    ct).ConfigureAwait(false);
                if (error is not null)
                {
                    return Fail(error);
                }
            }

            var bookFolders = new Dictionary<string, string?>(StringComparer.Ordinal);
            // One lookup per book folder (through its first file, as a folder's own ancestors leave the folder out).
            // shortcut: one request per book on every fetch; page the books' Folder items instead if libraries get large.
            foreach (var book in shared.Where(i => i.Type == "AudioBook" && !string.IsNullOrEmpty(i.ParentId)).DistinctBy(i => i.ParentId))
            {
                (bookFolders[book.ParentId!], error) = await BookFolderAsync(peer, book.Id!, ct).ConfigureAwait(false);
                if (error is not null)
                {
                    return Fail(error);
                }
            }

            var items = shared.Select(i => Map(i, series, bookFolders)).ToList();

            var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(items)));
            if (string.Equals(hash, etag, StringComparison.Ordinal))
            {
                return new PeerFetchResult(false, true, null, null);
            }

            return new PeerFetchResult(true, false, new CatalogResponse(ProtocolInfo.Version, CleanName(publicInfo?.ServerName), hash, items), null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail("timeout");
        }
        catch (JsonException)
        {
            return Fail("invalid catalog");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return Fail("connection failed: " + PeerClient.Redact(ex.Message, peer.Token));
        }
    }

    /// <inheritdoc />
    public async Task<Stream?> GetImageAsync(RemotePeer peer, string itemId, string type, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(peer);
        var uri = PeerUri.Build(peer.BaseUrl, "Items/" + Uri.EscapeDataString(itemId) + "/Images/" + Uri.EscapeDataString(type));
        if (uri is null)
        {
            return null;
        }

        using var request = CreateRequest(HttpMethod.Get, uri, peer.Id, peer.Token);
        return await PeerClient.DownloadImageAsync(_client(), request, _timeout, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<HttpResponseMessage> OpenStreamAsync(RemotePeer peer, CatalogItem item, string? range, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(item);
        // Audio is never capped or transcoded: the friend's limit is for video.
        var isAudio = MediaTypes.IsAudio(item.Type);
        var bitRate = isAudio ? 0 : item.Media?.BitRate ?? 0;
        var limit = bitRate > 0 ? await GetBitrateLimitAsync(peer, ct).ConfigureAwait(false) : 0;
        var transcode = limit > 0 && bitRate > limit;

        // shortcut: transcoded account streams cannot seek; upgrade to HLS if owners use bitrate limits a lot.
        var query = transcode
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"static=false&container=mkv&videoCodec=h264&audioCodec=aac&videoBitRate={Math.Max(limit - AudioBitRate, MinVideoBitRate)}&audioBitRate={AudioBitRate}&deviceId={DeviceId(peer.Id)}&playSessionId={Guid.NewGuid():N}")
            : "static=true";
        var uri = PeerUri.Build(peer.BaseUrl, (isAudio ? "Audio/" : "Videos/") + Uri.EscapeDataString(item.Id) + "/stream?" + query)
            ?? throw new InvalidOperationException("invalid base url");

        // A GET has no content, so the request message can be disposed once the headers arrived (see PeerClient).
        using var request = CreateRequest(HttpMethod.Get, uri, peer.Id, peer.Token);
        if (!transcode && !string.IsNullOrWhiteSpace(range))
        {
            request.Headers.TryAddWithoutValidation("Range", range);
        }

        try
        {
            return await _client().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new HttpRequestException("connection failed: " + PeerClient.Redact(ex.Message, peer.Token), null, ex.StatusCode);
        }
    }

    private async Task<int> GetBitrateLimitAsync(RemotePeer peer, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (_policies.TryGetValue(peer.Id, out var cached)
            && string.Equals(cached.Token, peer.Token, StringComparison.Ordinal)
            && now - cached.FetchedAt < _policyLifetime)
        {
            return cached.Limit;
        }

        var uri = PeerUri.Build(peer.BaseUrl, "Users/Me");
        if (uri is null)
        {
            return 0;
        }

        try
        {
            var (me, _) = await GetJsonAsync<JfUser>(uri, peer.Id, peer.Token, _timeout, ct).ConfigureAwait(false);
            if (me is null)
            {
                // Not cached, so the next stream asks again; until then play the original.
                return 0;
            }

            var limit = Math.Max(me.Policy?.RemoteClientBitrateLimit ?? 0, 0);
            _policies[peer.Id] = (peer.Token, limit, now);
            return limit;
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return 0;
        }
    }

    private async Task<string?> GetServerNameAsync(string baseUrl, Guid peerId, string token, CancellationToken ct)
    {
        var uri = PeerUri.Build(baseUrl, "System/Info/Public");
        try
        {
            var (info, _) = await GetJsonAsync<JfPublicInfo>(uri!, peerId, token, _timeout, ct).ConfigureAwait(false);
            return info is null ? null : CleanName(info.ServerName);
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            // The token is already issued; a missing name must not lose it.
            return null;
        }
    }

    /// <summary>
    /// Gets a book file's folder names below its library root (never an absolute path), like <c>AudiobookFolders</c>.
    /// A failed lookup leaves the book loose (no folder) instead of failing the whole catalog; only a rejected sign-in
    /// (HTTP 401) is returned as an error, so the peer is signed in again.
    /// </summary>
    private async Task<(string? Folder, string? Error)> BookFolderAsync(RemotePeer peer, string itemId, CancellationToken ct)
    {
        var user = string.IsNullOrEmpty(peer.AccountUserId) ? string.Empty : "?userId=" + Uri.EscapeDataString(peer.AccountUserId);
        var uri = PeerUri.Build(peer.BaseUrl, "Items/" + Uri.EscapeDataString(itemId) + "/Ancestors" + user);
        List<JfItem?>? ancestors;
        string? error;
        try
        {
            (ancestors, error) = await GetJsonAsync<List<JfItem?>>(uri!, peer.Id, peer.Token, _catalogTimeout, ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            (ancestors, error) = (null, "invalid response");
        }

        if (ancestors is null)
        {
            if (error?.StartsWith(AccountPeerInfo.SignInFailedPrefix, StringComparison.Ordinal) == true)
            {
                return (null, error);
            }

            _logger.LogWarning(
                "JellyShare: folder of an audiobook from peer {Peer} could not be read ({Error}); it is listed without its folder",
                peer.Id,
                PeerClient.Redact(error ?? "no response", peer.Token));
            return (null, null);
        }

        // Nearest first; the folders between the file and its library are the book's path.
        var root = ancestors.FindIndex(a => a?.Type == "CollectionFolder");
        if (root <= 0)
        {
            return (null, null);
        }

        var names = ancestors.Take(root).Select(a => a?.Name).Reverse().ToList();
        return names.Any(string.IsNullOrWhiteSpace) ? (null, null) : (string.Join('/', names), null);
    }

    private async Task<string?> PageAsync(RemotePeer peer, string types, string fields, Action<JfItem> add, CancellationToken ct)
    {
        var user = string.IsNullOrEmpty(peer.AccountUserId) ? string.Empty : "userId=" + Uri.EscapeDataString(peer.AccountUserId) + "&";
        var start = 0;
        string? previousFirst = null;
        for (var pages = 0; pages < MaxPages; pages++)
        {
            var uri = PeerUri.Build(
                peer.BaseUrl,
                string.Create(CultureInfo.InvariantCulture, $"Items?{user}Recursive=true&IncludeItemTypes={types}&Fields={fields}&StartIndex={start}&Limit={PageSize}"));
            var (page, error) = await GetJsonAsync<JfItemsPage>(uri!, peer.Id, peer.Token, _catalogTimeout, ct).ConfigureAwait(false);
            if (page is null)
            {
                return error;
            }

            var items = page.Items ?? [];
            var first = items.OfType<JfItem>().FirstOrDefault()?.Id;
            if (first is not null && first == previousFirst)
            {
                // A server that ignores StartIndex would answer the same page until the page cap.
                return "server repeats pages";
            }

            previousFirst = first;
            foreach (var item in items.OfType<JfItem>())
            {
                add(item);
            }

            // The server may answer fewer items than asked for; an empty page ends paging whatever the count says.
            start += items.Count;
            if (items.Count == 0 || start >= page.TotalRecordCount)
            {
                return null;
            }
        }

        return "too many pages";
    }

    private async Task<(T? Value, string? Error)> GetJsonAsync<T>(Uri uri, Guid peerId, string? token, TimeSpan timeout, CancellationToken ct)
        where T : class
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var request = CreateRequest(HttpMethod.Get, uri, peerId, token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _client().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return (null, AccountPeerInfo.SignInFailedPrefix + ": HTTP 401");
        }

        var error = StatusError(response.StatusCode);
        if (error is not null)
        {
            return (null, error);
        }

        var value = await response.Content.ReadFromJsonAsync<T>(_jsonOptions, cts.Token).ConfigureAwait(false);
        return value is null ? throw new JsonException("empty response") : (value, null);
    }

    private static string? StatusError(HttpStatusCode status)
    {
        var code = ((int)status).ToString(CultureInfo.InvariantCulture);
        return code[0] switch
        {
            '2' => null,
            '3' => "HTTP " + code + " (redirects are not followed)",
            _ => "HTTP " + code,
        };
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, Guid peerId, string? token)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.UserAgent.Add(_userAgent);
        var auth = "MediaBrowser Client=\"JellyShare\", Device=\"JellyShare\", DeviceId=\"" + DeviceId(peerId) + "\", Version=\"" + _version + "\"";
        if (!string.IsNullOrEmpty(token))
        {
            auth += ", Token=\"" + token + "\"";
        }

        request.Headers.TryAddWithoutValidation("Authorization", auth);
        return request;
    }

    private static string DeviceId(Guid peerId) => "jellyshare-" + peerId.ToString("N", CultureInfo.InvariantCulture);

    private static bool IsShared(JfItem i, RemotePeer peer)
        => !string.IsNullOrWhiteSpace(i.Id)
            && i.Type is not null
            && MediaTypes.IsRequested(peer, i.Type)
            && i.LocationType != "Virtual"
            && i.MediaSources is [{ Protocol: "File" }, ..];

    private static CatalogItem Map(JfItem i, Dictionary<string, Dictionary<string, string>> series, Dictionary<string, string?> bookFolders)
    {
        if (MediaTypes.IsAudio(i.Type))
        {
            return MapAudio(i, bookFolders);
        }

        var source = i.MediaSources![0]!;
        var streams = (source.MediaStreams ?? []).OfType<JfMediaStream>().ToList();
        var video = streams.Find(s => s.Type == "Video");
        var media = new MediaInfo(
            source.Container,
            video?.Codec,
            video?.Width,
            video?.Height,
            source.Bitrate,
            streams.Where(s => s.Type == "Audio").Select(s => new AudioTrack(s.Language, s.Codec, s.Channels, s.DisplayTitle)).ToArray(),
            streams.Where(s => s.Type == "Subtitle").Select(s => new SubtitleTrack(s.Language, s.Codec, s.DisplayTitle)).ToArray());
        var images = (i.ImageTags?.Keys ?? Enumerable.Empty<string>()).ToList();
        if (i.BackdropImageTags is { Count: > 0 })
        {
            images.Add("Backdrop");
        }

        return new CatalogItem(
            i.Id!,
            i.Type!,
            i.Name ?? string.Empty,
            i.ProductionYear,
            Ids(i.ProviderIds),
            i.SeriesName,
            i.SeriesId is not null && series.TryGetValue(i.SeriesId, out var seriesIds) ? seriesIds : null,
            i.ParentIndexNumber,
            i.IndexNumber,
            i.RunTimeTicks,
            i.Overview,
            media,
            images.ToArray(),
            i.DateCreated ?? DateTime.UnixEpoch,
            i.SeriesId);
    }

    private static CatalogItem MapAudio(JfItem i, Dictionary<string, string?> bookFolders)
    {
        var source = i.MediaSources![0]!;
        var audio = (source.MediaStreams ?? []).OfType<JfMediaStream>()
            .Where(s => s.Type == "Audio")
            .Select(s => new AudioTrack(s.Language, s.Codec, s.Channels, s.DisplayTitle))
            .ToArray();
        var isBook = i.Type == "AudioBook";
        var folder = isBook && i.ParentId is not null ? bookFolders.GetValueOrDefault(i.ParentId) : null;

        // Like a JellyShare provider: a book's art holder is its folder (none directly in the library root), a track's its album.
        var bookHolder = folder is null ? null : i.ParentId;
        var holder = isBook ? bookHolder : i.AlbumId;
        var images = (i.ImageTags?.Keys ?? Enumerable.Empty<string>()).ToList();
        var holderHasPrimary = isBook ? holder is not null && i.ParentPrimaryImageItemId == holder : i.AlbumPrimaryImageTag is not null;
        if (holderHasPrimary && !images.Contains("Primary"))
        {
            images.Add("Primary");
        }

        var albumArtist = i.AlbumArtists?.OfType<JfNamedItem>().Select(a => a.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? i.AlbumArtist;
        return new CatalogItem(
            i.Id!,
            i.Type!,
            i.Name ?? string.Empty,
            i.ProductionYear,
            Ids(i.ProviderIds),
            null,
            null,
            null,
            null,
            i.RunTimeTicks,
            i.Overview,
            new MediaInfo(source.Container, null, null, null, source.Bitrate, audio, []),
            images.ToArray(),
            i.DateCreated ?? DateTime.UnixEpoch,
            null,
            i.Album,
            albumArtist,
            i.Artists?.OfType<string>().ToArray(),
            i.ParentIndexNumber,
            i.IndexNumber,
            holder,
            folder);
    }

    private static Dictionary<string, string> Ids(Dictionary<string, string?>? ids)
        => ids?.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value!) ?? new Dictionary<string, string>();

    private static string CleanName(string? name)
        => new((name ?? string.Empty).Where(c => !char.IsControl(c)).Take(MaxServerNameLength).ToArray());

    private static AccountSignInResult SignInFail(string error) => new(false, null, null, null, error);

    private static PeerFetchResult Fail(string error) => new(false, false, null, error);
}
