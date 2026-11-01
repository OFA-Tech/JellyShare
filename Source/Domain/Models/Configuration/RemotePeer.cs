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

    /// <inheritdoc />
    public override string ToString() => $"RemotePeer({Id}, {Name})";
}
