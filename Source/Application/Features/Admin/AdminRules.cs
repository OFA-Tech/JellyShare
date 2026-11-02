using JellyShare.Domain.Enums;
using JellyShare.Domain.Models.Admin;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Protocol;
using JellyShare.Domain.Models.Sync;
using JellyShare.Domain.Rules;

namespace JellyShare.Application.Features.Admin;

/// <summary>Validation rules, views and the settings lock shared by the admin use cases. The messages are shown verbatim on the admin page.</summary>
public static class AdminRules
{
    /// <summary>Maximum length of a share or peer name.</summary>
    public const int MaxNameLength = 100;

    /// <summary>Maximum concurrent streams of a share.</summary>
    public const int MaxStreamsLimit = 100;

    /// <summary>Smallest concurrent playback limit of an account peer.</summary>
    public const int MinAccountStreams = 1;

    /// <summary>Largest concurrent playback limit of an account peer.</summary>
    public const int MaxAccountStreams = 20;

    /// <summary>Maximum length of a peer token.</summary>
    public const int MaxTokenLength = 512;

    /// <summary>Maximum bitrate cap of a share, in bits per second.</summary>
    public const int MaxBitrateBps = 2_000_000_000;

    /// <summary>The message for a peer URL that cannot be used.</summary>
    public const string InvalidUrlMessage =
        "The URL is not valid. Use the full address of the other server, for example http://host:8096 or https://host/jellyfin.";

    /// <summary>The message for a missing peer token.</summary>
    public const string MissingTokenMessage = "Enter the token from the invitation.";

    /// <summary>
    /// Serialises every read-modify-write of the settings by the admin use cases (one lock for the process, as the
    /// settings are one document).
    /// </summary>
    internal static readonly object SettingsLock = new();

    /// <summary>Trims a name and checks it: non-empty, at most <see cref="MaxNameLength"/> characters, no control characters.</summary>
    /// <param name="name">The name as typed.</param>
    /// <returns>The clean name, or <c>null</c> when it cannot be used.</returns>
    public static string? CleanName(string? name)
    {
        var trimmed = name?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxNameLength || trimmed.Any(char.IsControl) ? null : trimmed;
    }

    /// <summary>Normalizes a peer URL (see <see cref="PeerUri.Normalize"/>).</summary>
    /// <param name="url">The URL as typed.</param>
    /// <returns>The normalized URL, or <c>null</c>.</returns>
    public static string? NormalizeUrl(string? url) => PeerUri.Normalize(url);

    /// <summary>Checks a (trimmed, non-empty) token for length and control characters.</summary>
    /// <param name="token">The token.</param>
    /// <returns>The problem, or <c>null</c>.</returns>
    public static string? TokenError(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (token.Length > MaxTokenLength)
        {
            return $"The token is too long (at most {MaxTokenLength} characters). Check that you pasted only the token.";
        }

        return token.Any(char.IsControl)
            ? "The token contains line breaks or other control characters. Paste only the token."
            : null;
    }

    /// <summary>Validates a share's fields, in the order the admin page expects the messages.</summary>
    /// <param name="rawName">The name as typed.</param>
    /// <param name="libraryIds">The selected libraries.</param>
    /// <param name="maxStreams">The stream limit.</param>
    /// <param name="maxBitrate">The bitrate cap, or <c>null</c>.</param>
    /// <param name="knownLibraries">Returns the ids of the libraries on this server (read only when needed).</param>
    /// <param name="name">The clean name.</param>
    /// <param name="libs">The distinct selected libraries.</param>
    /// <returns>The first problem, or <c>null</c>.</returns>
    public static string? ValidateShare(
        string? rawName,
        IReadOnlyList<Guid>? libraryIds,
        int? maxStreams,
        int? maxBitrate,
        Func<IEnumerable<Guid>> knownLibraries,
        out string name,
        out List<Guid> libs)
    {
        ArgumentNullException.ThrowIfNull(knownLibraries);
        name = string.Empty;
        libs = new List<Guid>();
        var clean = CleanName(rawName);
        if (clean is null)
        {
            return "Enter a name for the invitation (at most 100 characters).";
        }

        if (maxStreams is null or < 1 or > MaxStreamsLimit)
        {
            return $"Max streams must be a number between 1 and {MaxStreamsLimit}.";
        }

        if (maxBitrate is <= 0 or > MaxBitrateBps)
        {
            return $"Max bitrate must be between 0 and {MaxBitrateBps / 1_000_000} Mbps, or empty for no limit.";
        }

        var wanted = (libraryIds ?? []).Distinct().ToList();
        if (wanted.Count == 0)
        {
            return "Pick at least one library to share.";
        }

        var known = knownLibraries().ToHashSet();
        if (wanted.Any(id => !known.Contains(id)))
        {
            return "One of the selected libraries does not exist.";
        }

        name = clean;
        libs = wanted;
        return null;
    }

    /// <summary>Compares two library folder paths the way Jellyfin stores them (slashes, trailing separator, OS case rules).</summary>
    /// <param name="a">The first path.</param>
    /// <param name="b">The second path.</param>
    /// <returns><c>true</c> when they are the same folder.</returns>
    public static bool SamePath(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        static string Norm(string p) => p.Trim().Replace('\\', '/').TrimEnd('/');
        return string.Equals(Norm(a), Norm(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>Describes a placeholder root and the library holding it.</summary>
    /// <param name="path">The root.</param>
    /// <param name="libraries">The libraries on this server.</param>
    /// <returns>The view.</returns>
    public static PlaceholderRootView RootView(string path, IReadOnlyList<LibraryInfo> libraries)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        var holder = string.IsNullOrWhiteSpace(path) ? null : libraries.FirstOrDefault(l => l.Locations.Any(loc => SamePath(loc, path)));
        return new PlaceholderRootView(path, holder is not null, holder?.Id, holder?.Name, holder?.AutomaticSeriesGrouping);
    }

    /// <summary>Describes a share (never its token or hash).</summary>
    /// <param name="share">The share.</param>
    /// <param name="activeStreams">The streams active now.</param>
    /// <returns>The view.</returns>
    public static ShareView ToView(PeerShare share, int activeStreams)
    {
        ArgumentNullException.ThrowIfNull(share);
        return new ShareView(share.Id, share.Name, share.Enabled, share.LibraryIds.ToList(), share.MaxStreams, share.MaxBitrate, activeStreams);
    }

    /// <summary>Describes a peer (never its token) with its sync state.</summary>
    /// <param name="peer">The peer.</param>
    /// <param name="states">The persisted peer states.</param>
    /// <returns>The view.</returns>
    public static PeerView ToView(RemotePeer peer, IReadOnlyDictionary<Guid, PeerState> states)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(states);
        states.TryGetValue(peer.Id, out var state);
        return new PeerView(
            peer.Id,
            peer.Name,
            peer.BaseUrl,
            peer.Enabled,
            peer.Priority,
            !string.IsNullOrEmpty(peer.Token),
            state?.LastSuccessUtc,
            state?.FailingSinceUtc,
            state?.LastError,
            state?.LastGoodCatalog?.Items?.Count,
            peer.Kind,
            peer.Username,
            peer.MaxStreams,
            peer.LimitAction,
            peer.Kind == PeerKind.Account && state?.LastError?.StartsWith(AccountPeerInfo.SignInFailedPrefix, StringComparison.Ordinal) == true);
    }

    /// <summary>Orders peers by priority, then by their position in the settings.</summary>
    /// <param name="peers">The peers.</param>
    /// <returns>The ordered peers.</returns>
    public static List<RemotePeer> ByPriority(IEnumerable<RemotePeer> peers)
        => peers
            .Select((p, i) => (Peer: p, Index: i))
            .OrderBy(x => x.Peer.Priority)
            .ThenBy(x => x.Index)
            .Select(x => x.Peer)
            .ToList();
}
