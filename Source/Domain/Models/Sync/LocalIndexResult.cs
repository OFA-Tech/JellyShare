using JellyShare.Domain.Models.Library;

namespace JellyShare.Domain.Models.Sync;

/// <summary>The titles the owner has locally.</summary>
/// <param name="MovieKeys">Keys of local movies whose file exists.</param>
/// <param name="EpisodeKeys">Keys of local episodes whose file exists.</param>
/// <param name="Series">Local series keyed <c>tvdb:{id}</c>/<c>imdb:{id}</c>/<c>tmdb:{id}</c>.</param>
/// <param name="UncheckedFiles">
/// Files counted as present without proof: not checked within the time budget, or probe errors other than not-found.
/// </param>
/// <param name="MissingFiles">Files that were checked and do not exist.</param>
public sealed record LocalIndexResult(
    ISet<string> MovieKeys,
    ISet<string> EpisodeKeys,
    IReadOnlyDictionary<string, LocalSeries> Series,
    int UncheckedFiles,
    int MissingFiles)
{
    /// <summary>Gets the keys for a media type.</summary>
    /// <param name="mediaType"><c>Movie</c> or <c>Episode</c>.</param>
    /// <returns>The keys.</returns>
    public ISet<string> KeysFor(string mediaType)
        => string.Equals(mediaType, "Movie", StringComparison.OrdinalIgnoreCase) ? MovieKeys : EpisodeKeys;
}
