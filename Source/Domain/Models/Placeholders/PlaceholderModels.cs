namespace JellyShare.Domain.Models.Placeholders;

/// <summary>A placeholder file to create or overwrite.</summary>
/// <param name="RelativePath">Path relative to the placeholder root, with forward slashes.</param>
/// <param name="Content">The UTF-8 text content.</param>
public sealed record FileWrite(string RelativePath, string Content);

/// <summary>The changes needed to bring a placeholder root in line with the wanted entries.</summary>
/// <param name="Writes">Files to write, ordered by path.</param>
/// <param name="Deletes">Planner-owned files (<c>.strm</c>/<c>.nfo</c>) to delete, ordered by path.</param>
/// <param name="TouchedDirs">Distinct relative directories that have a write or delete, ordered.</param>
/// <param name="Skipped">Entries skipped because their placeholder location was not a safe relative path.</param>
public sealed record PlaceholderDiff(
    IReadOnlyList<FileWrite> Writes,
    IReadOnlyList<string> Deletes,
    IReadOnlyList<string> TouchedDirs,
    int Skipped = 0);

/// <summary>Fetches an image for a placeholder folder.</summary>
/// <param name="topDir">The relative movie or series folder (first path segment).</param>
/// <param name="imageType"><c>Primary</c> or <c>Backdrop</c>.</param>
/// <param name="ct">The cancellation token.</param>
/// <returns>The image bytes, or <c>null</c> when the peer has none.</returns>
public delegate Task<Stream?> PlaceholderImageFetcher(string topDir, string imageType, CancellationToken ct);

/// <summary>What applying a <see cref="PlaceholderDiff"/> did.</summary>
/// <param name="Written">Placeholder files (<c>.strm</c>/<c>.nfo</c>) written.</param>
/// <param name="Deleted">Placeholder files deleted.</param>
/// <param name="Refused">Diff entries refused because their path was unsafe.</param>
/// <param name="ImagesWritten">Images downloaded.</param>
/// <param name="ChangedDirs">Full paths of directories that changed (including removed ones), ordered.</param>
public sealed record PlaceholderWriteResult(int Written, int Deleted, int Refused, int ImagesWritten, IReadOnlyList<string> ChangedDirs)
{
    /// <summary>Gets the relative paths written (committed), with the hash of what was written.</summary>
    public IReadOnlyDictionary<string, string> WrittenPaths { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets the relative paths deleted (or already gone).</summary>
    public IReadOnlyList<string> DeletedPaths { get; init; } = [];

    /// <summary>Gets the movie/series folders (relative, first segment) that got a new <c>.strm</c>.</summary>
    public IReadOnlyList<string> NewStrmTops { get; init; } = [];
}
