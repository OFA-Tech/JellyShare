using Jellyfin.Plugin.JellyShare.Consumer;

namespace Jellyfin.Plugin.JellyShare.Provider;

/// <summary>Body of <c>POST admin/shares</c>.</summary>
/// <param name="Name">The display name of the peer the invitation is for.</param>
/// <param name="LibraryIds">The libraries the peer may see.</param>
/// <param name="MaxStreams">The concurrent stream limit.</param>
/// <param name="MaxBitrate">The bitrate cap in bits per second, or <c>null</c> for no cap.</param>
public sealed record CreateShareRequest(string? Name, List<Guid>? LibraryIds, int? MaxStreams, int? MaxBitrate);

/// <summary>Body of <c>PUT admin/shares/{id}</c>. Replaces name, libraries, limits and the enabled flag; the token is untouched.</summary>
/// <param name="Name">The display name.</param>
/// <param name="LibraryIds">The libraries the peer may see.</param>
/// <param name="MaxStreams">The concurrent stream limit.</param>
/// <param name="MaxBitrate">The bitrate cap in bits per second, or <c>null</c> for no cap.</param>
/// <param name="Enabled">Whether the share is active.</param>
public sealed record UpdateShareRequest(string? Name, List<Guid>? LibraryIds, int? MaxStreams, int? MaxBitrate, bool? Enabled);

/// <summary>Response of <c>POST admin/shares</c>. The only place the plaintext token is ever returned.</summary>
/// <param name="Id">The share id.</param>
/// <param name="Name">The share name.</param>
/// <param name="Token">The plaintext token (shown once).</param>
public sealed record CreatedShareResponse(Guid Id, string Name, string Token);

/// <summary>Body of <c>POST admin/peers</c>.</summary>
/// <param name="Name">The display name.</param>
/// <param name="BaseUrl">The base URL of the remote Jellyfin server.</param>
/// <param name="Token">The token the remote server gave us.</param>
/// <param name="Enabled">Whether the peer is used.</param>
public sealed record AddPeerRequest(string? Name, string? BaseUrl, string? Token, bool? Enabled);

/// <summary>Body of <c>PUT admin/peers/{id}</c>. An empty token keeps the stored one.</summary>
/// <param name="Name">The display name.</param>
/// <param name="BaseUrl">The base URL of the remote Jellyfin server.</param>
/// <param name="Token">A new token, or empty to keep the stored one.</param>
/// <param name="Enabled">Whether the peer is used.</param>
public sealed record UpdatePeerRequest(string? Name, string? BaseUrl, string? Token, bool? Enabled);

/// <summary>Body of <c>POST admin/peers/order</c>.</summary>
/// <param name="Ids">The peer ids, most preferred first.</param>
public sealed record ReorderPeersRequest(List<Guid>? Ids);

/// <summary>Body of <c>POST admin/peers/test</c>. The token is optional when testing a saved peer.</summary>
/// <param name="Url">The base URL to test.</param>
/// <param name="Token">The token to test with.</param>
/// <param name="PeerId">A saved peer whose stored token is used when <paramref name="Token"/> is empty.</param>
public sealed record TestPeerRequest(string? Url, string? Token, Guid? PeerId = null);

/// <summary>Result of <c>POST admin/peers/test</c>.</summary>
/// <param name="Ok">The connection works.</param>
/// <param name="ServerName">The remote server name.</param>
/// <param name="ProtocolVersion">The remote protocol version.</param>
/// <param name="Titles">The number of titles the token gives access to.</param>
/// <param name="Error">A readable error (never contains the token).</param>
public sealed record TestPeerResponse(bool Ok, string? ServerName, string? ProtocolVersion, int? Titles, string? Error);

/// <summary>Body of <c>POST admin/libraries/attach</c>.</summary>
/// <param name="LibraryId">The library that receives the placeholder root.</param>
/// <param name="Type"><c>Movies</c> or <c>Shows</c>: which of the plugin's own placeholder roots to add.</param>
public sealed record AttachLibraryRequest(Guid LibraryId, string? Type);

/// <summary>A share as shown on the admin page. Never contains the token or its hash.</summary>
/// <param name="Id">The share id.</param>
/// <param name="Name">The share name.</param>
/// <param name="Enabled">Whether the share is active.</param>
/// <param name="LibraryIds">The shared libraries.</param>
/// <param name="MaxStreams">The concurrent stream limit.</param>
/// <param name="MaxBitrate">The bitrate cap in bits per second, or <c>null</c>.</param>
/// <param name="ActiveStreams">The streams currently active.</param>
public sealed record ShareView(Guid Id, string Name, bool Enabled, IReadOnlyList<Guid> LibraryIds, int MaxStreams, int? MaxBitrate, int ActiveStreams);

/// <summary>A peer as shown on the admin page. Never contains the token.</summary>
/// <param name="Id">The peer id.</param>
/// <param name="Name">The display name.</param>
/// <param name="BaseUrl">The base URL.</param>
/// <param name="Enabled">Whether the peer is used.</param>
/// <param name="Priority">The priority; lower is preferred.</param>
/// <param name="HasToken">A token is stored.</param>
/// <param name="LastSuccessUtc">When the peer last answered.</param>
/// <param name="FailingSinceUtc">When the current run of failures started.</param>
/// <param name="LastError">The last error.</param>
/// <param name="Titles">The titles in the last good catalog, if any.</param>
public sealed record PeerView(
    Guid Id,
    string Name,
    string BaseUrl,
    bool Enabled,
    int Priority,
    bool HasToken,
    DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? FailingSinceUtc,
    string? LastError,
    int? Titles);

/// <summary>A placeholder root and where it is attached.</summary>
/// <param name="Path">The root folder.</param>
/// <param name="Attached">The root is a path of a library of the right type.</param>
/// <param name="LibraryId">The library holding the root.</param>
/// <param name="LibraryName">The name of that library.</param>
/// <param name="AutomaticSeriesGrouping">Series grouping flag of that library (shows only).</param>
public sealed record PlaceholderRootView(string Path, bool Attached, Guid? LibraryId, string? LibraryName, bool? AutomaticSeriesGrouping);

/// <summary>Response of <c>GET admin/status</c>.</summary>
/// <param name="ProtocolVersion">This server's protocol version.</param>
/// <param name="ServerName">This server's name.</param>
/// <param name="Shares">The shares offered.</param>
/// <param name="Peers">The peers consumed, in priority order.</param>
/// <param name="LastSync">The last sync report.</param>
/// <param name="Movies">The movie placeholder root.</param>
/// <param name="Shows">The show placeholder root.</param>
/// <param name="Libraries">The libraries on this server.</param>
/// <param name="SyncIntervalHours">The hours between syncs.</param>
public sealed record StatusResponse(
    string ProtocolVersion,
    string ServerName,
    IReadOnlyList<ShareView> Shares,
    IReadOnlyList<PeerView> Peers,
    SyncReport? LastSync,
    PlaceholderRootView Movies,
    PlaceholderRootView Shows,
    IReadOnlyList<LibraryInfo> Libraries,
    int SyncIntervalHours);

/// <summary>A Jellyfin library, reduced to what the admin page needs.</summary>
/// <param name="Id">The library id.</param>
/// <param name="Name">The library name.</param>
/// <param name="CollectionType"><c>movies</c>, <c>tvshows</c> or other/mixed (<c>null</c>).</param>
/// <param name="Locations">The folders of the library.</param>
/// <param name="AutomaticSeriesGrouping">The library option <c>EnableAutomaticSeriesGrouping</c>.</param>
public sealed record LibraryInfo(Guid Id, string Name, string? CollectionType, IReadOnlyList<string> Locations, bool AutomaticSeriesGrouping);

/// <summary>Library operations needed by the admin page, abstracted so the controller stays unit-testable.</summary>
public interface IAdminLibraries
{
    /// <summary>Lists the libraries.</summary>
    /// <returns>The libraries.</returns>
    IReadOnlyList<LibraryInfo> List();

    /// <summary>Adds a folder to a library and refreshes it (one scan, so the root gets its folder item).</summary>
    /// <param name="libraryName">The library name.</param>
    /// <param name="path">The folder.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task that completes when the path is added; the scan continues in the background.</returns>
    Task AddPathAsync(string libraryName, string path, CancellationToken ct);
}

/// <summary>Persists the plugin configuration after the admin page changed it.</summary>
public interface IConfigSaver
{
    /// <summary>Saves the current configuration.</summary>
    void Save();
}

/// <summary><see cref="IConfigSaver"/> saving through <see cref="Plugin.Instance"/>.</summary>
public sealed class PluginConfigSaver : IConfigSaver
{
    /// <inheritdoc />
    public void Save() => Plugin.Instance?.SaveConfiguration();
}

/// <summary>Runs syncs and exposes the last report (implemented by <see cref="SyncService"/>).</summary>
public interface ISyncRunner
{
    /// <summary>Gets the report of the last finished run, if any.</summary>
    SyncReport? LastReport { get; }

    /// <summary>Runs one sync; concurrent calls wait for the one in progress.</summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The report.</returns>
    Task<SyncReport> RunAsync(CancellationToken ct);
}
