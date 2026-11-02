using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using JellyShare.Domain.Interfaces.Peers;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Peers;
using JellyShare.Domain.Models.Protocol;
using JellyShare.Domain.Rules;

namespace JellyShare.Data.Peers.Clients;

/// <summary>
/// Client for a friend's plain Jellyfin server reached with a user account (an account peer). The password is only sent
/// in the sign-in body; the access token only in the <c>Authorization</c> header.
/// </summary>
public sealed class AccountPeerClient : IPeerClient, IAccountSignIn
{
    private const int PageSize = 500;
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
    private readonly ConcurrentDictionary<Guid, (int Limit, DateTimeOffset FetchedAt)> _policies = new();

    /// <summary>Initializes a new instance of the <see cref="AccountPeerClient"/> class.</summary>
    /// <param name="client">Returns the HTTP client to use (the redirect-free <see cref="PeerClient.HttpClientName"/> client).</param>
    /// <param name="clock">The clock for the bitrate-policy cache; defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="timeout">Timeout for sign-in, image and policy requests; defaults to <see cref="PeerClient.DefaultTimeout"/>.</param>
    /// <param name="catalogTimeout">Timeout for each catalog page; defaults to <see cref="PeerClient.CatalogTimeout"/>.</param>
    public AccountPeerClient(Func<HttpClient> client, TimeProvider? clock = null, TimeSpan? timeout = null, TimeSpan? catalogTimeout = null)
    {
        _client = client;
        _clock = clock ?? TimeProvider.System;
        _timeout = timeout ?? PeerClient.DefaultTimeout;
        _catalogTimeout = catalogTimeout ?? timeout ?? PeerClient.CatalogTimeout;
    }

    /// <inheritdoc />
    /// <remarks>Not used: <see cref="PeerClientRouter"/> sends pings to the token client.</remarks>
    public Task<PeerPingResult> PingAsync(string baseUrl, CancellationToken ct)
        => throw new NotSupportedException("Pings go to the token peer client.");

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

            var series = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            error = await PageAsync(peer, "Series", "ProviderIds", s => series[s.Id ?? string.Empty] = Ids(s.ProviderIds), ct).ConfigureAwait(false);
            if (error is not null)
            {
                return Fail(error);
            }

            var items = new List<CatalogItem>();
            error = await PageAsync(
                peer,
                "Movie,Episode",
                "ProviderIds,Overview,MediaSources,DateCreated&EnableImages=true",
                i =>
                {
                    if (IsShared(i))
                    {
                        items.Add(Map(i, series));
                    }
                },
                ct).ConfigureAwait(false);
            if (error is not null)
            {
                return Fail(error);
            }

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
        var bitRate = item.Media?.BitRate ?? 0;
        var limit = bitRate > 0 ? await GetBitrateLimitAsync(peer, ct).ConfigureAwait(false) : 0;
        var transcode = limit > 0 && bitRate > limit;

        // shortcut: transcoded account streams cannot seek; upgrade to HLS if owners use bitrate limits a lot.
        var query = transcode
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"static=false&container=mkv&videoCodec=h264&audioCodec=aac&videoBitRate={Math.Max(limit - AudioBitRate, MinVideoBitRate)}&audioBitRate={AudioBitRate}&deviceId={DeviceId(peer.Id)}")
            : "static=true";
        var uri = PeerUri.Build(peer.BaseUrl, "Videos/" + Uri.EscapeDataString(item.Id) + "/stream?" + query)
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
        if (_policies.TryGetValue(peer.Id, out var cached) && now - cached.FetchedAt < _policyLifetime)
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
            _policies[peer.Id] = (limit, now);
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

    private async Task<string?> PageAsync(RemotePeer peer, string types, string fields, Action<JfItem> add, CancellationToken ct)
    {
        var user = string.IsNullOrEmpty(peer.AccountUserId) ? string.Empty : "userId=" + Uri.EscapeDataString(peer.AccountUserId) + "&";
        var total = 1;
        for (var start = 0; start < total; start += PageSize)
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
            foreach (var item in items.OfType<JfItem>())
            {
                add(item);
            }

            // An empty page ends the loop even if the count says otherwise.
            total = items.Count == 0 ? 0 : page.TotalRecordCount;
        }

        return null;
    }

    private async Task<(T? Value, string? Error)> GetJsonAsync<T>(Uri uri, Guid peerId, string? token, TimeSpan timeout, CancellationToken ct)
        where T : class
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var request = CreateRequest(HttpMethod.Get, uri, peerId, token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _client().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        var error = StatusError(response.StatusCode);
        if (error is not null)
        {
            return (null, error == "HTTP 401" ? AccountPeerInfo.SignInFailedPrefix + ": " + error : error);
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

    private static bool IsShared(JfItem i)
        => !string.IsNullOrWhiteSpace(i.Id)
            && i.Type is "Movie" or "Episode"
            && i.LocationType != "Virtual"
            && i.MediaSources is [{ Protocol: "File" }, ..];

    private static CatalogItem Map(JfItem i, Dictionary<string, Dictionary<string, string>> series)
    {
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

    private static Dictionary<string, string> Ids(Dictionary<string, string?>? ids)
        => ids?.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value!) ?? new Dictionary<string, string>();

    private static string CleanName(string? name)
        => new((name ?? string.Empty).Where(c => !char.IsControl(c)).Take(MaxServerNameLength).ToArray());

    private static AccountSignInResult SignInFail(string error) => new(false, null, null, null, error);

    private static PeerFetchResult Fail(string error) => new(false, false, null, error);
}
