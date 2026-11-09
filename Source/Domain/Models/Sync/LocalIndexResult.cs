using JellyShare.Domain.Enums;
using JellyShare.Domain.Models.Library;

namespace JellyShare.Domain.Models.Sync;

/// <summary>The titles the owner has locally.</summary>
/// <param name="Keys">
/// Keys of local titles whose file exists, per media type. Music and audiobooks also hold the album/book title key of
/// every local track, so a friend's album the owner has any track of is recognised.
/// </param>
/// <param name="Series">Local series keyed <c>tvdb:{id}</c>/<c>imdb:{id}</c>/<c>tmdb:{id}</c>.</param>
/// <param name="UncheckedFiles">
/// Files counted as present without proof: not checked within the time budget, or probe errors other than not-found.
/// </param>
/// <param name="MissingFiles">Files that were checked and do not exist.</param>
public sealed record LocalIndexResult(
    IReadOnlyDictionary<MediaType, ISet<string>> Keys,
    IReadOnlyDictionary<string, LocalSeries> Series,
    int UncheckedFiles,
    int MissingFiles)
{
    /// <summary>Initializes a new instance of the <see cref="LocalIndexResult"/> class with movie and episode keys only.</summary>
    /// <param name="movieKeys">Keys of local movies whose file exists.</param>
    /// <param name="episodeKeys">Keys of local episodes whose file exists.</param>
    /// <param name="series">Local series.</param>
    /// <param name="uncheckedFiles">Files counted as present without proof.</param>
    /// <param name="missingFiles">Files that were checked and do not exist.</param>
    public LocalIndexResult(ISet<string> movieKeys, ISet<string> episodeKeys, IReadOnlyDictionary<string, LocalSeries> series, int uncheckedFiles, int missingFiles)
        : this(new Dictionary<MediaType, ISet<string>> { [MediaType.Movies] = movieKeys, [MediaType.Shows] = episodeKeys }, series, uncheckedFiles, missingFiles)
    {
    }

    /// <summary>Gets the owner's artist names (of their own tracks), keyed by the normalized name.</summary>
    public IReadOnlyDictionary<string, string> Artists { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Gets the keys of local movies.</summary>
    public ISet<string> MovieKeys => KeysOf(MediaType.Movies);

    /// <summary>Gets the keys of local episodes.</summary>
    public ISet<string> EpisodeKeys => KeysOf(MediaType.Shows);

    /// <summary>Gets the keys of a media type.</summary>
    /// <param name="type">The media type.</param>
    /// <returns>The keys; empty when there are none.</returns>
    public ISet<string> KeysOf(MediaType type) => Keys.TryGetValue(type, out var keys) ? keys : new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Gets the keys for a media type.</summary>
    /// <param name="mediaType"><c>Movie</c> or <c>Episode</c>.</param>
    /// <returns>The keys.</returns>
    public ISet<string> KeysFor(string mediaType)
        => string.Equals(mediaType, "Movie", StringComparison.OrdinalIgnoreCase) ? MovieKeys : EpisodeKeys;
}
