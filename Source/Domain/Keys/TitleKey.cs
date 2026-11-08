namespace JellyShare.Domain.Keys;

/// <summary>
/// The key a "stream" is counted under. A movie or episode is one title; the tracks of an album (or the files of an
/// audiobook) are one title too, so playing through an album uses one stream slot.
/// </summary>
public static class TitleKey
{
    /// <summary>Returns the title key of an entry.</summary>
    /// <param name="entry">The merged entry.</param>
    /// <returns>The album/book key for music and audiobooks (when the item names one), else the canonical key.</returns>
    public static string For(MergedEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Sources.Count == 0)
        {
            return entry.CanonicalKey;
        }

        var item = entry.Sources[0].Item;
        if (string.Equals(item.Type, "Audio", StringComparison.Ordinal)
            && KeyText.Normalize(item.Album).Length > 0)
        {
            return $"album:name:{KeyText.Hash(item.AlbumArtist ?? item.Artists?.FirstOrDefault(), item.Album)}";
        }

        if (string.Equals(item.Type, "AudioBook", StringComparison.Ordinal)
            && KeyText.Normalize(item.FolderPath).Length > 0)
        {
            return $"book:{KeyText.Hash(item.FolderPath)}";
        }

        return entry.CanonicalKey;
    }
}
