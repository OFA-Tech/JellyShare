using System.Text;
using Jellyfin.Plugin.JellyShare.Configuration;

namespace Jellyfin.Plugin.JellyShare.Placeholders;

/// <summary>Placeholder content and location helpers.</summary>
public static class PlaceholderPaths
{
    /// <summary>Builds the content of a <c>.strm</c> file. It contains no secrets.</summary>
    /// <param name="relayBase">The loopback relay base URL; a trailing slash is tolerated.</param>
    /// <param name="canonicalKey">The canonical key of the entry.</param>
    /// <returns>The relay URL.</returns>
    public static string StrmContent(string relayBase, string canonicalKey)
    {
        ArgumentNullException.ThrowIfNull(relayBase);
        ArgumentNullException.ThrowIfNull(canonicalKey);
        return relayBase.TrimEnd('/') + "/JellyShare/v1/play/" + Uri.EscapeDataString(canonicalKey);
    }

    /// <summary>Checks whether a path lies inside one of the configured placeholder roots.</summary>
    /// <param name="path">The item path.</param>
    /// <param name="cfg">The plugin configuration.</param>
    /// <returns><c>true</c> if the path is inside a placeholder root.</returns>
    public static bool IsPlaceholder(string? path, PluginConfiguration cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalized = Normalize(path);
        return IsInside(normalized, Normalize(cfg.MoviesPlaceholderPath))
            || IsInside(normalized, Normalize(cfg.ShowsPlaceholderPath));
    }

    private static bool IsInside(string path, string root)
        => root.Length > 0
            && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && (path.Length == root.Length || path[root.Length] == '/');

    private static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(path.Length);
        foreach (var c in path.Trim().Replace('\\', '/'))
        {
            if (c == '/' && sb.Length > 0 && sb[^1] == '/')
            {
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString().TrimEnd('/');
    }
}
