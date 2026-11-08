using JellyShare.Domain.Enums;

namespace JellyShare.Domain.Models.Configuration;

/// <summary>A share offered to one peer. Only the hash of the share token is stored.</summary>
public class PeerShare
{
    /// <summary>Gets or sets the share id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the display name of the peer this share is for.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the lowercase hex SHA-256 of the share token.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the share is active.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the ids of the libraries the peer may see.</summary>
    public List<Guid> LibraryIds { get; set; } = new();

    /// <summary>Gets or sets the maximum concurrent streams for the peer.</summary>
    public int MaxStreams { get; set; } = 2;

    /// <summary>Gets or sets the bitrate cap in bits per second; <c>null</c> means no cap.</summary>
    public int? MaxBitrate { get; set; }

    /// <summary>
    /// Gets or sets whether the share offers the default media types (<c>true</c>), its own <see cref="Types"/> (<c>false</c>),
    /// or <c>null</c> for a share saved before media types existed, which offers what its libraries hold.
    /// </summary>
    public bool? UseDefaultTypes { get; set; }

    /// <summary>Gets or sets the media types offered when <see cref="UseDefaultTypes"/> is <c>false</c>.</summary>
    public List<MediaType> Types { get; set; } = new();

    /// <inheritdoc />
    public override string ToString() => $"PeerShare({Id}, {Name})";
}
