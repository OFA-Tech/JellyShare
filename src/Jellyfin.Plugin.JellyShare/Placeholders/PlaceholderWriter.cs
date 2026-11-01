using Jellyfin.Plugin.JellyShare.Consumer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.JellyShare.Placeholders;

/// <summary>Fetches an image for a placeholder folder.</summary>
/// <param name="topDir">The relative movie or series folder (first path segment).</param>
/// <param name="imageType"><c>Primary</c> or <c>Backdrop</c>.</param>
/// <param name="ct">The cancellation token.</param>
/// <returns>The image bytes, or <c>null</c> when the peer has none.</returns>
public delegate Task<Stream?> PlaceholderImageFetcher(string topDir, string imageType, CancellationToken ct);

/// <summary>What <see cref="PlaceholderWriter.ApplyAsync"/> did.</summary>
/// <param name="Written">Placeholder files (<c>.strm</c>/<c>.nfo</c>) written.</param>
/// <param name="Deleted">Placeholder files deleted.</param>
/// <param name="Refused">Diff entries refused because their path was unsafe.</param>
/// <param name="ImagesWritten">Images downloaded.</param>
/// <param name="ChangedDirs">Full paths of directories that changed (including removed ones), ordered.</param>
public sealed record PlaceholderWriteResult(int Written, int Deleted, int Refused, int ImagesWritten, IReadOnlyList<string> ChangedDirs);

/// <summary>Thrown when applying a diff fails part-way; <see cref="Partial"/> holds what was already changed.</summary>
public sealed class PlaceholderWriteException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="PlaceholderWriteException"/> class.</summary>
    /// <param name="partial">What was done before the failure.</param>
    /// <param name="inner">The cause.</param>
    public PlaceholderWriteException(PlaceholderWriteResult partial, Exception inner)
        : base("Applying placeholder changes failed part-way", inner)
    {
        Partial = partial;
    }

    /// <summary>Gets what was done before the failure.</summary>
    public PlaceholderWriteResult Partial { get; }
}

/// <summary>Applies a <see cref="PlaceholderDiff"/> to a plugin-owned placeholder root.</summary>
public sealed class PlaceholderWriter
{
    /// <summary>The marker file kept in every root so scans never skip it as empty. Never deleted.</summary>
    public const string MarkerName = ".jellyshare";

    private static readonly (string Type, string FileName)[] Images = [("Primary", "poster.jpg"), ("Backdrop", "fanart.jpg")];

    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="PlaceholderWriter"/> class.</summary>
    /// <param name="logger">The logger.</param>
    public PlaceholderWriter(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Gets or sets a test hook called with (temp path, target path) before a placeholder file is committed.</summary>
    internal Action<string, string>? BeforeCommit { get; set; }

    /// <summary>Lists the placeholder files (<c>.strm</c>/<c>.nfo</c>) under a root with their content hash.</summary>
    /// <param name="root">The placeholder root.</param>
    /// <returns>Relative path (forward slashes) to <see cref="PlaceholderPlanner.Hash"/> of the content.</returns>
    public static IReadOnlyDictionary<string, string> ScanExisting(string root)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return result;
        }

        var rootFull = Path.GetFullPath(root);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        foreach (var file in Directory.EnumerateFiles(rootFull, "*", options))
        {
            if (!IsOwned(file))
            {
                continue;
            }

            var rel = Path.GetRelativePath(rootFull, file).Replace('\\', '/');
            if (Resolve(rootFull, rel) is null)
            {
                continue;
            }

            try
            {
                result[rel] = PlaceholderPlanner.Hash(File.ReadAllText(file));
            }
            catch (IOException)
            {
            }
        }

        return result;
    }

    /// <summary>
    /// Applies the diff: deletes, then atomic writes, then missing <c>poster.jpg</c>/<c>fanart.jpg</c> for every
    /// movie/series folder that got a new <c>.strm</c>, then removes folders left without any <c>.strm</c> and empty
    /// directories (never the root or its marker). Paths that could leave the root are refused.
    /// </summary>
    /// <param name="diff">The diff from <see cref="PlaceholderPlanner.Plan"/>.</param>
    /// <param name="root">The placeholder root.</param>
    /// <param name="imageFetcher">Fetches folder images; failures are logged and ignored.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>What was done.</returns>
    public async Task<PlaceholderWriteResult> ApplyAsync(
        PlaceholderDiff diff,
        string root,
        PlaceholderImageFetcher? imageFetcher,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var rootFull = Path.GetFullPath(root);
        EnsureRoot(rootFull);

        var changed = new SortedSet<string>(StringComparer.Ordinal);
        var removedTops = new SortedSet<string>(StringComparer.Ordinal);
        int written = 0, deleted = 0, refused = 0, images = 0;

        try
        {
            foreach (var rel in diff.Deletes)
            {
                ct.ThrowIfCancellationRequested();
                var full = IsOwned(rel) ? ResolveSafe(rootFull, rel) : null;
                if (full is null)
                {
                    refused++;
                    _logger.LogWarning("JellyShare: refused to delete unsafe placeholder path");
                    continue;
                }

                if (File.Exists(full))
                {
                    File.Delete(full);
                    deleted++;
                }

                changed.Add(Path.GetDirectoryName(full)!);
                removedTops.Add(TopDir(rel));
            }

            var strmTops = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var write in diff.Writes)
            {
                ct.ThrowIfCancellationRequested();
                var full = IsOwned(write.RelativePath) ? ResolveSafe(rootFull, write.RelativePath) : null;
                if (full is null)
                {
                    refused++;
                    _logger.LogWarning("JellyShare: refused to write unsafe placeholder path");
                    continue;
                }

                AtomicFile.WriteAllText(full, write.Content, BeforeCommit);
                written++;
                changed.Add(Path.GetDirectoryName(full)!);
                if (write.RelativePath.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
                {
                    strmTops.Add(TopDir(write.RelativePath));
                }
            }

            if (imageFetcher is not null)
            {
                foreach (var top in strmTops)
                {
                    var topFull = Path.Combine(rootFull, top);
                    foreach (var (type, fileName) in Images)
                    {
                        var target = Path.Combine(topFull, fileName);
                        if (File.Exists(target) || HasReparsePoint(rootFull, target))
                        {
                            continue;
                        }

                        if (await TryDownloadAsync(imageFetcher, top, type, target, ct).ConfigureAwait(false))
                        {
                            images++;
                            changed.Add(topFull);
                        }
                    }
                }
            }

            foreach (var top in removedTops)
            {
                RemoveIfNoStrm(rootFull, top);
            }

            foreach (var dir in changed)
            {
                RemoveEmptyUpwards(rootFull, dir);
            }
        }
        catch (Exception ex)
        {
            // Files already changed on disk must still be scanned: hand the partial result to the caller.
            throw new PlaceholderWriteException(new PlaceholderWriteResult(written, deleted, refused, images, changed.ToList()), ex);
        }

        return new PlaceholderWriteResult(written, deleted, refused, images, changed.ToList());
    }

    /// <summary>Resolves a relative placeholder path inside the root, or returns <c>null</c> if it is unsafe.</summary>
    /// <param name="rootFull">The full root path.</param>
    /// <param name="relativePath">A relative path with forward slashes: at least one directory plus a file name.</param>
    /// <returns>The full path, or <c>null</c>.</returns>
    internal static string? Resolve(string rootFull, string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) || Path.IsPathRooted(relativePath))
        {
            return null;
        }

        var slash = relativePath.LastIndexOf('/');
        if (slash <= 0 || !PathNaming.IsSafeRelative(relativePath[..slash], relativePath[(slash + 1)..]))
        {
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(rootFull, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return IsStrictlyInside(rootFull, full) ? full : null;
    }

    /// <summary>
    /// Checks whether the target or any existing directory between the root (exclusive) and the target is a
    /// reparse point (symlink, junction). Unreadable attributes count as a reparse point.
    /// </summary>
    /// <param name="rootFull">The full root path.</param>
    /// <param name="full">The full target path inside the root.</param>
    /// <returns><c>true</c> when the path goes through a link.</returns>
    internal static bool HasReparsePoint(string rootFull, string full)
    {
        var current = full;
        while (IsStrictlyInside(rootFull, current))
        {
            try
            {
                if ((File.Exists(current) || Directory.Exists(current))
                    && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                {
                    return true;
                }
            }
            catch (Exception)
            {
                return true;
            }

            current = Path.GetDirectoryName(current);
            if (current is null)
            {
                return false;
            }
        }

        return false;
    }

    private static string? ResolveSafe(string rootFull, string relativePath)
    {
        var full = Resolve(rootFull, relativePath);
        return full is null || HasReparsePoint(rootFull, full) ? null : full;
    }

    internal static bool IsStrictlyInside(string rootFull, string full)
    {
        var prefix = Path.TrimEndingDirectorySeparator(rootFull) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return full.Length > prefix.Length && full.StartsWith(prefix, comparison);
    }

    private static bool IsOwned(string path)
        => path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".nfo", StringComparison.OrdinalIgnoreCase);

    private static string TopDir(string rel) => rel[..rel.IndexOf('/', StringComparison.Ordinal)];

    private static void EnsureRoot(string rootFull)
    {
        Directory.CreateDirectory(rootFull);
        var marker = Path.Combine(rootFull, MarkerName);
        if (!File.Exists(marker))
        {
            AtomicFile.WriteAllText(marker, "JellyShare placeholder folder. Managed by the JellyShare plugin.\n");
        }
    }

    private async Task<bool> TryDownloadAsync(PlaceholderImageFetcher fetcher, string top, string type, string target, CancellationToken ct)
    {
        try
        {
            var stream = await fetcher(top, type, ct).ConfigureAwait(false);
            if (stream is null)
            {
                return false;
            }

            await using (stream.ConfigureAwait(false))
            {
                await AtomicFile.WriteStreamAsync(target, stream, ct).ConfigureAwait(false);
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning("JellyShare: {Type} image for a placeholder folder could not be downloaded: {Error}", type, ex.GetType().Name);
            return false;
        }
    }

    private void RemoveIfNoStrm(string rootFull, string top)
    {
        var topFull = Path.GetFullPath(Path.Combine(rootFull, top));
        if (!IsStrictlyInside(rootFull, topFull) || !Directory.Exists(topFull) || HasReparsePoint(rootFull, topFull))
        {
            return;
        }

        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            if (Directory.EnumerateFiles(topFull, "*.strm", options).Any() || ContainsReparsePoint(topFull))
            {
                return;
            }

            Directory.Delete(topFull, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("JellyShare: could not remove placeholder folder: {Error}", ex.GetType().Name);
        }
    }

    private static bool ContainsReparsePoint(string dir)
    {
        var pending = new Stack<string>();
        pending.Push(dir);
        while (pending.Count > 0)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    return true;
                }

                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    pending.Push(entry);
                }
            }
        }

        return false;
    }

    private static void RemoveEmptyUpwards(string rootFull, string dir)
    {
        var current = dir;
        while (IsStrictlyInside(rootFull, current) && Directory.Exists(current) && !HasReparsePoint(rootFull, current))
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(current).Any())
                {
                    return;
                }

                Directory.Delete(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            current = Path.GetDirectoryName(current)!;
        }
    }
}
