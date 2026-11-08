namespace JellyShare.Domain.Models.Library;

/// <summary>A local movie, episode, track or audiobook file, with just what is needed for its keys.</summary>
/// <param name="Type"><c>Movie</c>, <c>Episode</c>, <c>Audio</c> or <c>AudioBook</c>.</param>
/// <param name="Path">The media file path.</param>
/// <param name="ProviderIds">The item's provider ids.</param>
/// <param name="SeriesProviderIds">For episodes, the series' provider ids.</param>
/// <param name="SeasonNumber">For episodes, the season number.</param>
/// <param name="EpisodeNumber">For episodes, the episode number.</param>
/// <param name="Name">For audio, the title.</param>
/// <param name="Album">For tracks, the album.</param>
/// <param name="AlbumArtist">For tracks, the first album artist.</param>
/// <param name="Artists">For tracks, the artists.</param>
/// <param name="DiscNumber">For tracks, the disc number.</param>
/// <param name="TrackNumber">For tracks, the track number.</param>
/// <param name="FolderPath">For audiobooks, the folder names below the library root (<c>Author X/Book Y</c>).</param>
public sealed record LocalMedia(
    string Type,
    string? Path,
    IReadOnlyDictionary<string, string>? ProviderIds,
    IReadOnlyDictionary<string, string>? SeriesProviderIds,
    int? SeasonNumber,
    int? EpisodeNumber,
    string? Name = null,
    string? Album = null,
    string? AlbumArtist = null,
    IReadOnlyList<string>? Artists = null,
    int? DiscNumber = null,
    int? TrackNumber = null,
    string? FolderPath = null);

/// <summary>A local series.</summary>
/// <param name="Name">The series title.</param>
/// <param name="Path">The series folder.</param>
/// <param name="ProviderIds">The series provider ids.</param>
public sealed record LocalSeriesItem(string Name, string? Path, IReadOnlyDictionary<string, string>? ProviderIds);

/// <summary>Everything the local index reads from the library, in one pass.</summary>
/// <param name="Media">All movies and episodes in all libraries.</param>
/// <param name="Series">All series in all libraries.</param>
public sealed record LocalLibrarySnapshot(IReadOnlyList<LocalMedia> Media, IReadOnlyList<LocalSeriesItem> Series)
{
    /// <summary>Gets the configured library folder paths (all libraries).</summary>
    public IReadOnlyList<string> Locations { get; init; } = [];
}

/// <summary>
/// Which local titles a targeted reconcile needs: movies with any of <see cref="MovieIds"/>, series with any of
/// <see cref="SeriesIds"/> and their episodes. Provider kinds and ids are lowercase.
/// </summary>
/// <param name="MovieIds">Movie provider ids by kind (<c>tmdb</c>, <c>imdb</c>).</param>
/// <param name="SeriesIds">Series provider ids by kind (<c>tvdb</c>, <c>imdb</c>, <c>tmdb</c>).</param>
public sealed record LibraryScope(
    IReadOnlyDictionary<string, IReadOnlySet<string>> MovieIds,
    IReadOnlyDictionary<string, IReadOnlySet<string>> SeriesIds)
{
    /// <summary>Gets a value indicating whether nothing is asked for.</summary>
    public bool IsEmpty => MovieIds.Count == 0 && SeriesIds.Count == 0;
}
