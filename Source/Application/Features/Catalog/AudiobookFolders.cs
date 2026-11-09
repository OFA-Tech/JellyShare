namespace JellyShare.Application.Features.Catalog;

/// <summary>Works out an audiobook's folder names relative to its library root.</summary>
public static class AudiobookFolders
{
    /// <summary>Gets the folder names between a library root and a book file, joined with <c>/</c>.</summary>
    /// <param name="filePath">The book's file path.</param>
    /// <param name="libraryRoots">The library's physical root folders.</param>
    /// <returns>For example <c>Author X/Book Y</c>; <c>null</c> when the file is directly in a root or under none.</returns>
    public static string? Relative(string? filePath, IEnumerable<string> libraryRoots)
    {
        ArgumentNullException.ThrowIfNull(libraryRoots);
        var directory = string.IsNullOrWhiteSpace(filePath) ? null : Path.GetDirectoryName(filePath);
        if (directory is null)
        {
            return null;
        }

        foreach (var root in libraryRoots.Where(r => !string.IsNullOrWhiteSpace(r)).OrderByDescending(r => r.Length))
        {
            var relative = Path.GetRelativePath(root, directory);
            var parts = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            if (Path.IsPathRooted(relative) || parts.Length == 0 || parts[0] == "." || parts[0] == "..")
            {
                continue;
            }

            return string.Join('/', parts);
        }

        return null;
    }
}
