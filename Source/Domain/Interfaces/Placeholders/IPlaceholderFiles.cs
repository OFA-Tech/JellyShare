using JellyShare.Domain.Models.Placeholders;

namespace JellyShare.Domain.Interfaces.Placeholders;

/// <summary>
/// The file system side of the plugin-owned placeholder roots: atomic writes, the <c>.jellyshare</c> marker, refusal
/// of paths that leave the root or go through links, and the folder images.
/// </summary>
public interface IPlaceholderFiles
{
    /// <summary>
    /// Applies the diff: deletes, then atomic writes, then missing images for folders that got a new <c>.strm</c>
    /// (when <paramref name="imageFetcher"/> is given), then removes folders left without any <c>.strm</c>.
    /// </summary>
    /// <param name="diff">The diff.</param>
    /// <param name="root">The placeholder root.</param>
    /// <param name="imageFetcher">Fetches folder images; <c>null</c> to skip images.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>What was done.</returns>
    /// <exception cref="Exceptions.PlaceholderWriteException">Failed part-way; carries what was already changed.</exception>
    Task<PlaceholderWriteResult> ApplyAsync(PlaceholderDiff diff, string root, PlaceholderImageFetcher? imageFetcher, CancellationToken ct);

    /// <summary>Downloads missing folder images, a few folders at a time, only into folders that still hold a <c>.strm</c>.</summary>
    /// <param name="root">The placeholder root.</param>
    /// <param name="tops">The folders (relative, first segment).</param>
    /// <param name="fetcher">Fetches the images.</param>
    /// <param name="parallelism">How many folders are fetched at once.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The images written and the full paths of the folders that got one.</returns>
    Task<(int Images, IReadOnlyList<string> ChangedDirs)> DownloadImagesAsync(
        string root,
        IReadOnlyCollection<string> tops,
        PlaceholderImageFetcher fetcher,
        int parallelism,
        CancellationToken ct);

    /// <summary>Checks that a root may be used (new, empty, or carrying the marker).</summary>
    /// <param name="root">The placeholder root.</param>
    /// <returns>A clear error, or <c>null</c> when the root may be used.</returns>
    string? RootProblem(string root);

    /// <summary>Creates the root and its marker; the marker is only ever created in a new or empty folder.</summary>
    /// <param name="root">The placeholder root.</param>
    /// <exception cref="InvalidOperationException">The folder is not empty and has no marker.</exception>
    void EnsureRoot(string root);

    /// <summary>Checks whether a safe relative placeholder path exists under the root.</summary>
    /// <param name="rootFull">The full root path.</param>
    /// <param name="relativePath">The relative path (forward slashes).</param>
    /// <returns><c>true</c> when the path is safe and the file exists.</returns>
    bool Exists(string rootFull, string relativePath);

    /// <summary>Checks whether a movie/series folder holds a placeholder and lacks an image.</summary>
    /// <param name="root">The placeholder root.</param>
    /// <param name="top">The folder (relative, first segment).</param>
    /// <returns><c>true</c> when an image is missing.</returns>
    bool MissesImages(string root, string top);

    /// <summary>Lists the placeholder files (<c>.strm</c>/<c>.nfo</c>) under a root with their content hash.</summary>
    /// <param name="root">The placeholder root.</param>
    /// <returns>Relative path (forward slashes) to content hash.</returns>
    IReadOnlyDictionary<string, string> ScanExisting(string root);

    /// <summary>Reads the content of every <c>.strm</c> under a root (links skipped); an unreadable root is logged and skipped.</summary>
    /// <param name="root">The placeholder root.</param>
    /// <returns>The trimmed contents.</returns>
    IReadOnlyList<string> ReadStrmContents(string root);
}
