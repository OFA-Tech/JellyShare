using Jellyfin.Plugin.JellyShare.Keys;

namespace Jellyfin.Plugin.JellyShare.Placeholders;

/// <summary>A series the owner already has locally; placeholders adopt its title and provider ids.</summary>
/// <param name="Name">The local series title.</param>
/// <param name="ProviderIds">The local series provider ids (for example Tvdb, Imdb, Tmdb).</param>
public sealed record LocalSeries(string Name, IReadOnlyDictionary<string, string> ProviderIds)
{
    /// <summary>Gets the lowercased, trimmed id of a provider, or <c>null</c> if absent.</summary>
    /// <param name="kind">The provider kind, for example <c>tvdb</c>.</param>
    /// <returns>The id or <c>null</c>.</returns>
    public string? IdOf(string kind) => Normalize(ProviderIds, kind);

    internal static string? Normalize(IReadOnlyDictionary<string, string>? ids, string kind)
    {
        if (ids is null)
        {
            return null;
        }

        foreach (var (name, value) in ids)
        {
            if (string.Equals(name, kind, StringComparison.OrdinalIgnoreCase) && MediaKey.NormalizeId(value) is { } id)
            {
                return id;
            }
        }

        return null;
    }

    internal static LocalSeries? Find(
        IReadOnlyDictionary<string, LocalSeries>? localSeries,
        IReadOnlyDictionary<string, string> seriesIds)
    {
        if (localSeries is null || localSeries.Count == 0)
        {
            return null;
        }

        foreach (var kind in SeriesKinds)
        {
            if (seriesIds.TryGetValue(kind, out var id)
                && localSeries.TryGetValue(kind + ":" + id, out var found))
            {
                return found;
            }
        }

        return null;
    }

    internal static readonly string[] SeriesKinds = ["tvdb", "imdb", "tmdb"];
}
