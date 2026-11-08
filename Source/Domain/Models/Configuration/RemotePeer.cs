using JellyShare.Domain.Enums;

namespace JellyShare.Domain.Models.Configuration;

/// <summary>A remote peer this server syncs from. Holds that peer's secret token.</summary>
public class RemotePeer
{
    /// <summary>Gets or sets the peer id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the base URL of the remote Jellyfin server.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the secret token the remote server gave us.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the peer is used.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the priority; lower is preferred.</summary>
    public int Priority { get; set; }

    /// <summary>Gets or sets how this server authenticates to the peer. For an account peer, <see cref="Token"/> is the Jellyfin access token.</summary>
    public PeerKind Kind { get; set; }

    /// <summary>Gets or sets the peer's user name (account peers only).</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Gets or sets the user id on the peer's server (account peers only).</summary>
    public string AccountUserId { get; set; } = string.Empty;

    /// <summary>Gets or sets the concurrent stream limit (account peers only).</summary>
    public int MaxStreams { get; set; } = 1;

    /// <summary>Gets or sets what happens when a new stream exceeds <see cref="MaxStreams"/>.</summary>
    public StreamLimitAction LimitAction { get; set; }

    /// <summary>Gets or sets a value indicating whether every media type is accepted from this peer. Old settings accept everything.</summary>
    public bool AcceptAllTypes { get; set; } = true;

    /// <summary>
    /// Gets or sets the media types accepted when <see cref="AcceptAllTypes"/> is <c>false</c>.
    /// <see cref="Copy"/> is shallow: assign a new list instead of changing this one.
    /// </summary>
    public List<MediaType> AcceptTypes { get; set; } = new();

    /// <summary>Creates a shallow copy, for copy-on-write updates.</summary>
    /// <returns>The copy.</returns>
    public RemotePeer Copy() => (RemotePeer)MemberwiseClone();

    /// <inheritdoc />
    public override string ToString() => $"RemotePeer({Id}, {Name})";
}
