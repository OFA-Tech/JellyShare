using JellyShare.Domain.Models.Protocol;

namespace JellyShare.Domain.Models.Sync;

/// <summary>What the consumer remembers about one peer between syncs.</summary>
public sealed class PeerState
{
    /// <summary>Gets or sets the last catalog fetched successfully.</summary>
    public CatalogResponse? LastGoodCatalog { get; set; }

    /// <summary>Gets or sets the ETag of <see cref="LastGoodCatalog"/>.</summary>
    public string? Etag { get; set; }

    /// <summary>Gets or sets when the peer last answered (200 or 304).</summary>
    public DateTimeOffset? LastSuccessUtc { get; set; }

    /// <summary>Gets or sets when the current run of failures started; <c>null</c> while the peer is healthy.</summary>
    public DateTimeOffset? FailingSinceUtc { get; set; }

    /// <summary>Gets or sets the last error (never contains the token).</summary>
    public string? LastError { get; set; }
}
