namespace JellyShare.Domain.Models.Placeholders;

/// <summary>
/// The fields the plugin writes on an audio placeholder item: Jellyfin reads none of them from a <c>.strm</c> file.
/// </summary>
/// <param name="Name">The track or chapter title.</param>
/// <param name="Artists">The track artists.</param>
/// <param name="AlbumArtists">The album artists (what groups the album under its artist).</param>
/// <param name="Album">The album name.</param>
/// <param name="IndexNumber">The track number.</param>
/// <param name="ParentIndexNumber">The disc number.</param>
/// <param name="Locked">Whether the name and people are locked against Jellyfin's probe.</param>
public sealed record TrackMetadata(
    string Name,
    IReadOnlyList<string> Artists,
    IReadOnlyList<string> AlbumArtists,
    string? Album,
    int? IndexNumber,
    int? ParentIndexNumber,
    bool Locked);

/// <summary>An audio placeholder item in the library.</summary>
/// <param name="ItemId">The library item id.</param>
/// <param name="Key">The key from its <c>.strm</c> relay URL.</param>
/// <param name="Metadata">Its metadata (current when read, wanted when written).</param>
public sealed record PlaceholderTrack(Guid ItemId, string Key, TrackMetadata Metadata);

/// <summary>What one read of the library gives the metadata writer.</summary>
/// <param name="Tracks">The audio placeholder items.</param>
/// <param name="OwnerArtists">The artist and album artist names of the owner's own (non-placeholder) audio.</param>
public sealed record PlaceholderAudio(IReadOnlyList<PlaceholderTrack> Tracks, IReadOnlyList<string> OwnerArtists);
