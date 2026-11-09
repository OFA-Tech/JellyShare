using JellyShare.Domain.Models.Protocol;

namespace JellyShare.Domain.Models.Library;

/// <summary>A local movie or episode together with the data needed to filter it.</summary>
/// <param name="Id">The item id.</param>
/// <param name="Type"><c>Movie</c> or <c>Episode</c>.</param>
/// <param name="Path">The file path of the item.</param>
/// <param name="SeriesId">For episodes, the id of the owning series.</param>
/// <param name="Factory">Creates the catalog entry. Only called for items that pass the share filters.</param>
public sealed record LocalItem(Guid Id, string Type, string? Path, Guid? SeriesId, Func<CatalogItem> Factory)
{
    /// <summary>Gets the item's last modification time (what the catalog ETag is computed from).</summary>
    public DateTime DateModified { get; init; }

    /// <summary>Creates the catalog entry for the item (may query the library; call only for included items).</summary>
    /// <returns>The catalog entry.</returns>
    public CatalogItem CreateItem() => Factory();
}

/// <summary>What the stream endpoint needs to know about a library item.</summary>
/// <param name="Type">The item type, e.g. <c>Movie</c>, <c>Episode</c> or <c>Series</c>.</param>
/// <param name="Name">The display name (for the usage log).</param>
/// <param name="Path">The file path, if any.</param>
/// <param name="Bitrate">The total bitrate in bits per second, when known.</param>
/// <param name="RunTimeTicks">The runtime in ticks, when known.</param>
/// <param name="TitleId">For audio, the album (tracks) or book folder (audiobooks) id: the key that counts one title as one stream.</param>
public sealed record StreamInfo(string Type, string Name, string? Path, long? Bitrate, long? RunTimeTicks = null, Guid? TitleId = null);
