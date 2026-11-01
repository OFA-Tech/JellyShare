using System.Diagnostics.CodeAnalysis;
using JellyShare.Data.Files.Stores;
using JellyShare.Domain.Exceptions;
using JellyShare.Domain.Interfaces.Placeholders;
using JellyShare.Domain.Models.Placeholders;
using JellyShare.Domain.Rules;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JellyShare.Data.Files.Placeholders;

/// <summary>Applies a <see cref="PlaceholderDiff"/> to a plugin-owned placeholder root.</summary>
public sealed class PlaceholderWriter : IPlaceholderFiles
{
    /// <summary>The marker file kept in every root so scans never skip it as empty. Never deleted.</summary>
    public const string MarkerName = ".jellyshare";

    private const string TypeOnlyLogging = "An expected disk or peer failure: only the exception type is logged, never its text (paths, peer URLs) or stack.";

    private static readonly (string Type, string FileName)[] _images = [("Primary", "poster.jpg"), ("Backdrop", "fanart.jpg")];

    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="PlaceholderWriter"/> class.</summary>
    /// <param name="logger">The logger.</param>
    public PlaceholderWriter(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    string? IPlaceholderFiles.RootProblem(string root) => RootProblem(root);

    /// <inheritdoc />
    void IPlaceholderFiles.EnsureRoot(string root) => EnsureRoot(root);

    /// <inheritdoc />
    bool IPlaceholderFiles.MissesImages(string root, string top) => MissesImages(root, top);

    /// <inheritdoc />
    IReadOnlyDictionary<string, string> IPlaceholderFiles.ScanExisting(string root) => ScanExisting(root);

    /// <inheritdoc />
    public bool Exists(string rootFull, string relativePath)
        => Resolve(rootFull, relativePath) is { } full && File.Exists(full);

    /// <inheritdoc />
    [SuppressMessage("Major Code Smell", "S6667:Logging in a catch clause should pass the caught exception as a parameter.", Justification = TypeOnlyLogging)]
    public IReadOnlyList<string> ReadStrmContents(string root)
    {
        var contents = new List<string>();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return contents;
        }

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        try
        {
            foreach (var strm in Directory.EnumerateFiles(root, "*.strm", options))
            {
                if (ReadTextOrNull(strm) is { } text)
                {
                    contents.Add(text.Trim());
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("JellyShare sweep: placeholder folder unreadable: {Error}", ex.GetType().Name);
        }

        return contents;
    }

    /// <summary>Test hook: called with each file <see cref="ScanExisting"/> reads (per async flow).</summary>
    internal static readonly AsyncLocal<Action<string>?> ReadObserver = new();

    /// <summary>Gets or sets a test hook called with (temp path, target path) before a placeholder file is committed.</summary>
    internal Action<string, string>? BeforeCommit { get; set; }

    /// <summary>Lists the placeholder files (<c>.strm</c>/<c>.nfo</c>) under a root with their content hash.</summary>
    /// <param name="root">The placeholder root.</param>
    /// <returns>Relative path (forward slashes) to <see cref="PlaceholderRules.Hash"/> of the content.</returns>
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

            if (HashOrNull(file) is { } hash)
            {
                result[rel] = hash;
            }
        }

        return result;
    }

    /// <summary>
    /// Applies the diff: deletes, then atomic writes, then missing <c>poster.jpg</c>/<c>fanart.jpg</c> for every
    /// movie/series folder that got a new <c>.strm</c>, then removes folders left without any <c>.strm</c> and empty
    /// directories (never the root or its marker). Paths that could leave the root are refused.
    /// </summary>
    /// <param name="diff">The diff from the planner.</param>
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
        var strmTops = new SortedSet<string>(StringComparer.Ordinal);
        var writtenPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        var deletedPaths = new List<string>();
        int written = 0, deleted = 0, refused = 0, images = 0;

        PlaceholderWriteResult Result() => new(written, deleted, refused, images, changed.ToList())
        {
            WrittenPaths = writtenPaths,
            DeletedPaths = deletedPaths,
            NewStrmTops = strmTops.ToList(),
        };

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

                deletedPaths.Add(rel);
                changed.Add(Path.GetDirectoryName(full)!);
                removedTops.Add(TopDir(rel));
            }

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
                writtenPaths[write.RelativePath] = PlaceholderRules.Hash(write.Content);
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
                    foreach (var (type, fileName) in _images)
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
            throw new PlaceholderWriteException(Result(), ex);
        }

        return Result();
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
        if (slash <= 0 || !PlaceholderRules.IsSafeRelative(relativePath[..slash], relativePath[(slash + 1)..]))
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

    /// <summary>
    /// Checks that a placeholder root may be used: it does not exist yet, is empty, or carries the
    /// <see cref="MarkerName"/> marker. A non-empty folder without the marker is someone else's folder.
    /// </summary>
    /// <param name="root">The placeholder root.</param>
    /// <returns>A clear error, or <c>null</c> when the root may be used.</returns>
    public static string? RootProblem(string root)
    {
        var rootFull = Path.GetFullPath(root);
        try
        {
            if (!Directory.Exists(rootFull)
                || File.Exists(Path.Combine(rootFull, MarkerName))
                || !Directory.EnumerateFileSystemEntries(rootFull).Any())
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"JellyShare: the placeholder folder '{root}' cannot be read ({ex.GetType().Name}).";
        }

        return $"JellyShare: the placeholder folder '{root}' already contains files but no {MarkerName} marker, so it is not a "
            + "JellyShare folder and nothing is written or deleted there. Choose a new, empty folder.";
    }

    /// <summary>Creates the root and its marker; the marker is only ever created in a new or empty folder.</summary>
    /// <param name="root">The placeholder root.</param>
    /// <exception cref="InvalidOperationException">The folder is not empty and has no marker.</exception>
    public static void EnsureRoot(string root)
    {
        if (RootProblem(root) is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        var rootFull = Path.GetFullPath(root);
        Directory.CreateDirectory(rootFull);
        var marker = Path.Combine(rootFull, MarkerName);
        if (!File.Exists(marker))
        {
            AtomicFile.WriteAllText(marker, "JellyShare placeholder folder. Managed by the JellyShare plugin.\n");
        }
    }

    /// <summary>Checks whether a movie/series folder holds a placeholder and lacks <c>poster.jpg</c> or <c>fanart.jpg</c>.</summary>
    /// <param name="root">The placeholder root.</param>
    /// <param name="top">The folder (relative, first segment).</param>
    /// <returns><c>true</c> when an image is missing.</returns>
    public static bool MissesImages(string root, string top)
    {
        var rootFull = Path.GetFullPath(root);
        var topFull = Path.GetFullPath(Path.Combine(rootFull, top));
        return IsStrictlyInside(rootFull, topFull)
            && Directory.Exists(topFull)
            && _images.Any(i => !File.Exists(Path.Combine(topFull, i.FileName)));
    }

    /// <summary>
    /// Downloads missing <c>poster.jpg</c>/<c>fanart.jpg</c> into movie/series folders, a few at a time. Runs without the
    /// sync lock, so a folder is only filled while it still holds a <c>.strm</c>; an image that lands in a folder
    /// removed meanwhile is taken out again.
    /// </summary>
    /// <param name="root">The placeholder root.</param>
    /// <param name="tops">The folders (relative, first segment).</param>
    /// <param name="fetcher">Fetches the images; failures are logged and ignored.</param>
    /// <param name="parallelism">How many folders are fetched at once.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The images written and the full paths of the folders that got one.</returns>
    public async Task<(int Images, IReadOnlyList<string> ChangedDirs)> DownloadImagesAsync(
        string root,
        IReadOnlyCollection<string> tops,
        PlaceholderImageFetcher fetcher,
        int parallelism,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tops);
        ArgumentNullException.ThrowIfNull(fetcher);
        var rootFull = Path.GetFullPath(root);
        var images = 0;
        var changed = new SortedSet<string>(StringComparer.Ordinal);
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism), CancellationToken = ct };
        await Parallel.ForEachAsync(tops.Distinct(StringComparer.Ordinal), options, async (top, token) =>
        {
            var topFull = Path.GetFullPath(Path.Combine(rootFull, top));
            if (!IsStrictlyInside(rootFull, topFull) || HasReparsePoint(rootFull, topFull) || !HoldsStrm(topFull))
            {
                return;
            }

            foreach (var (type, fileName) in _images)
            {
                var target = Path.Combine(topFull, fileName);
                if (File.Exists(target) || !await TryDownloadAsync(fetcher, top, type, target, token).ConfigureAwait(false))
                {
                    continue;
                }

                if (!HoldsStrm(topFull))
                {
                    // Removed by a reconcile while downloading: don't leave an image-only folder behind.
                    TryDelete(target);
                    RemoveEmptyUpwards(rootFull, topFull);
                    return;
                }

                Interlocked.Increment(ref images);
                lock (changed)
                {
                    changed.Add(topFull);
                }
            }
        }).ConfigureAwait(false);

        return (images, changed.ToList());
    }

    private static bool HoldsStrm(string topFull)
    {
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            return Directory.Exists(topFull) && Directory.EnumerateFiles(topFull, "*.strm", options).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the next sync retries the delete.
        }
    }

    private static string? ReadTextOrNull(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? HashOrNull(string file)
    {
        try
        {
            ReadObserver.Value?.Invoke(file);
            return PlaceholderRules.Hash(File.ReadAllText(file));
        }
        catch (IOException)
        {
            return null;
        }
    }

    [SuppressMessage("Major Code Smell", "S6667:Logging in a catch clause should pass the caught exception as a parameter.", Justification = TypeOnlyLogging)]
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

    [SuppressMessage("Major Code Smell", "S6667:Logging in a catch clause should pass the caught exception as a parameter.", Justification = TypeOnlyLogging)]
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
