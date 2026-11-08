using JellyShare.Domain.Enums;
using JellyShare.Domain.Models.Library;

namespace JellyShare.Domain.Interfaces.Library;

/// <summary>Reads local library content, abstracted so the catalog logic stays unit-testable.</summary>
public interface ILocalItemSource
{
    /// <summary>Lists the items of the given media types in every library.</summary>
    /// <param name="types">The media types to read; types this server cannot read are ignored.</param>
    /// <returns>The local items.</returns>
    IEnumerable<LocalItem> GetItems(IReadOnlySet<MediaType> types);

    /// <summary>
    /// Gets what decides whether one id is exposed, without reading the whole library: the item with that id, or the
    /// episodes of the series with that id, as far as they are of the given media types.
    /// </summary>
    /// <param name="itemId">The item or series id.</param>
    /// <param name="types">The shared media types.</param>
    /// <returns>The candidates (not yet filtered for placeholders or missing files).</returns>
    IEnumerable<LocalItem> GetExposureCandidates(Guid itemId, IReadOnlySet<MediaType> types);

    /// <summary>Gets the local image file of an item as Jellyfin recorded it.</summary>
    /// <param name="itemId">The item (or series) id.</param>
    /// <param name="imageType">One of <c>Primary</c>, <c>Backdrop</c>, <c>Logo</c>, <c>Thumb</c>.</param>
    /// <returns>The file path, or <c>null</c> when the item has no such local image.</returns>
    string? GetImagePath(Guid itemId, string imageType);

    /// <summary>Gets the file path, name and bitrate of an item without building a catalog entry.</summary>
    /// <param name="itemId">The item id.</param>
    /// <returns>The stream info, or <c>null</c> when the item does not exist.</returns>
    StreamInfo? GetStreamInfo(Guid itemId);
}
