using JellyShare.Domain.Keys;
using JellyShare.Domain.Models.Placeholders;

namespace JellyShare.Application.Features.Placeholders;

/// <summary>What the plugin writes on an audio placeholder item. Pure.</summary>
public static class TrackMetadataRules
{
    /// <summary>Gets the metadata an entry's placeholder item should carry (from the first source, like its path).</summary>
    /// <param name="entry">The merged entry.</param>
    /// <param name="ownerArtists">The owner's artist names (<see cref="OwnerArtists.Build"/>), used instead of the friend's spelling.</param>
    /// <returns>The wanted metadata, locked.</returns>
    public static TrackMetadata Wanted(MergedEntry entry, IReadOnlyDictionary<string, string>? ownerArtists = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var item = entry.Sources[0].Item;
        var artists = Names(item.Artists).Select(a => OwnerArtists.Resolve(ownerArtists, a)).Distinct(StringComparer.Ordinal).ToList();
        var albumArtist = TitleKey.AlbumArtistOf(item) is { } own ? OwnerArtists.Resolve(ownerArtists, own) : null;
        return new TrackMetadata(
            item.Name,
            artists.Count > 0 || albumArtist is null ? artists : [albumArtist],
            albumArtist is null ? [] : [albumArtist],
            string.IsNullOrWhiteSpace(item.Album) ? null : item.Album,
            item.TrackNumber,
            item.DiscNumber,
            true);
    }

    /// <summary>Gets what to write on an item, or <c>null</c> when it already carries the wanted values (no write, so no loop).</summary>
    /// <param name="current">The item's current values.</param>
    /// <param name="wanted">The wanted values.</param>
    /// <returns><paramref name="wanted"/> when anything differs, else <c>null</c>.</returns>
    public static TrackMetadata? ToWrite(TrackMetadata current, TrackMetadata wanted)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(wanted);
        return Same(current, wanted) ? null : wanted;
    }

    /// <summary>Compares two values field by field (lists in order).</summary>
    /// <param name="a">The first.</param>
    /// <param name="b">The second.</param>
    /// <returns><c>true</c> when equal.</returns>
    public static bool Same(TrackMetadata a, TrackMetadata b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return string.Equals(a.Name, b.Name, StringComparison.Ordinal)
            && a.Artists.SequenceEqual(b.Artists, StringComparer.Ordinal)
            && a.AlbumArtists.SequenceEqual(b.AlbumArtists, StringComparer.Ordinal)
            && string.Equals(a.Album, b.Album, StringComparison.Ordinal)
            && a.IndexNumber == b.IndexNumber
            && a.ParentIndexNumber == b.ParentIndexNumber
            && a.Locked == b.Locked;
    }

    private static List<string> Names(IEnumerable<string>? names)
        => names?.Where(n => KeyText.Normalize(n).Length > 0).ToList() ?? [];
}
