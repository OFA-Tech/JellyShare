using JellyShare.Domain.Enums;
using JellyShare.Domain.Models.Admin;

namespace JellyShare.Domain.Models.Configuration;

/// <summary>Rules for which <see cref="MediaType"/>s a share offers and a peer accepts.</summary>
public static class MediaTypes
{
    private static readonly MediaType[] _moviesAndShows = { MediaType.Movies, MediaType.Shows };

    /// <summary>Gets every media type, in enum order.</summary>
    public static IReadOnlyList<MediaType> All { get; } = Enum.GetValues<MediaType>();

    /// <summary>Gets the media types this version can share, ask peers for and place.</summary>
    public static IReadOnlyList<MediaType> Supported { get; } = new[] { MediaType.Movies, MediaType.Shows, MediaType.Music, MediaType.Audiobooks };

    /// <summary>Maps a catalog item type to its media type.</summary>
    /// <param name="catalogItemType">The catalog item type (<c>Movie</c>, <c>Episode</c>, <c>Audio</c>, <c>AudioBook</c>).</param>
    /// <returns>The media type, or <c>null</c> for an unknown type.</returns>
    public static MediaType? Of(string catalogItemType) => catalogItemType switch
    {
        "Movie" => MediaType.Movies,
        "Episode" => MediaType.Shows,
        "Audio" => MediaType.Music,
        "AudioBook" => MediaType.Audiobooks,
        _ => null,
    };

    /// <summary>Tells whether a catalog item type is audio (a music track or an audiobook file).</summary>
    /// <param name="catalogItemType">The catalog item type.</param>
    /// <returns><c>true</c> for <c>Audio</c> and <c>AudioBook</c>.</returns>
    public static bool IsAudio(string? catalogItemType)
        => catalogItemType is not null && Of(catalogItemType) is MediaType.Music or MediaType.Audiobooks;

    /// <summary>Maps a media type to its catalog item type (the reverse of <see cref="Of"/>).</summary>
    /// <param name="type">The media type.</param>
    /// <returns>The catalog item type.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The media type has no catalog item type yet.</exception>
    public static string CatalogItemType(MediaType type) => type switch
    {
        MediaType.Movies => "Movie",
        MediaType.Shows => "Episode",
        MediaType.Music => "Audio",
        MediaType.Audiobooks => "AudioBook",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>Maps a library collection type to the media types it holds.</summary>
    /// <param name="collectionType">The lowercase collection type, or <c>null</c> for a mixed library.</param>
    /// <returns>The media types; a mixed or unknown library holds movies and shows.</returns>
    public static IReadOnlyList<MediaType> FromLibrary(string? collectionType) => collectionType switch
    {
        "movies" => new[] { MediaType.Movies },
        "tvshows" => new[] { MediaType.Shows },
        "music" => new[] { MediaType.Music },
        "books" => new[] { MediaType.Audiobooks },
        _ => _moviesAndShows,
    };

    /// <summary>Gets the types a new share offers by default: the configured default, or all.</summary>
    /// <param name="cfg">The settings.</param>
    /// <returns>The types.</returns>
    public static IReadOnlySet<MediaType> DefaultShare(JellyShareSettings cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        return (cfg.DefaultShareTypes ?? (IEnumerable<MediaType>)All).ToHashSet();
    }

    /// <summary>Gets the types a share offers.</summary>
    /// <param name="share">The share.</param>
    /// <param name="cfg">The settings.</param>
    /// <param name="libraries">The libraries on this server, for a share saved before types existed.</param>
    /// <returns>The types.</returns>
    public static IReadOnlySet<MediaType> EffectiveShare(PeerShare share, JellyShareSettings cfg, IReadOnlyList<LibraryInfo> libraries)
    {
        ArgumentNullException.ThrowIfNull(share);
        ArgumentNullException.ThrowIfNull(libraries);
        return share.UseDefaultTypes switch
        {
            true => DefaultShare(cfg),
            false => share.Types.ToHashSet(),
            // A share saved before media types existed only ever served movies and shows; an update must not widen it.
            null => libraries.Where(l => share.LibraryIds.Contains(l.Id)).SelectMany(l => FromLibrary(l.CollectionType)).Intersect(_moviesAndShows).ToHashSet(),
        };
    }

    /// <summary>Gets the types this server accepts from a peer.</summary>
    /// <param name="peer">The peer.</param>
    /// <returns>The types.</returns>
    public static IReadOnlySet<MediaType> Accepted(RemotePeer peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        return (peer.AcceptAllTypes ? All : peer.AcceptTypes).ToHashSet();
    }

    /// <summary>Gets the types requested from a peer: what this server accepts from it and this version supports.</summary>
    /// <param name="peer">The peer.</param>
    /// <returns>The types in enum order; empty when none of the accepted types is supported yet.</returns>
    public static IReadOnlyList<MediaType> Requested(RemotePeer peer)
        => Accepted(peer).Intersect(Supported).Order().ToList();

    /// <summary>Tells whether a peer's item of the given catalog type is wanted from it (and placed by the sync).</summary>
    /// <param name="peer">The peer.</param>
    /// <param name="catalogItemType">The catalog item type.</param>
    /// <returns><c>true</c> when the type is accepted from the peer and supported.</returns>
    public static bool IsRequested(RemotePeer peer, string catalogItemType)
        => Of(catalogItemType) is { } type && Supported.Contains(type) && Accepted(peer).Contains(type);

    /// <summary>Formats types as a comma-separated list in enum order, without duplicates.</summary>
    /// <param name="types">The types.</param>
    /// <returns>For example <c>Movies,Shows</c>.</returns>
    public static string Format(IEnumerable<MediaType> types)
        => string.Join(',', types.Distinct().Order());

    /// <summary>Parses <see cref="Format"/> output. An absent value is a peer from before types existed: movies and shows.</summary>
    /// <param name="value">The text, or <c>null</c>.</param>
    /// <returns>The types; unknown names and numbers are ignored.</returns>
    public static IReadOnlySet<MediaType> ParseOrLegacy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return _moviesAndShows.ToHashSet();
        }

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => Enum.IsDefined(typeof(MediaType), n) ? Enum.Parse<MediaType>(n) : (MediaType?)null)
            .OfType<MediaType>()
            .ToHashSet();
    }
}
