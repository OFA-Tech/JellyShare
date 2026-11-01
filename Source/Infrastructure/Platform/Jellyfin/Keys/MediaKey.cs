using System.Globalization;
using JellyShare.Jellyfin.Protocol;

namespace JellyShare.Jellyfin.Keys;

/// <summary>
/// Derives the provider-id based identity keys used to merge the same title across peers.
/// </summary>
public static class MediaKey
{
    private static readonly string[] MovieKinds = ["tmdb", "imdb"];
    private static readonly string[] EpisodeKinds = ["tvdb", "tmdb"];

    /// <summary>Returns all keys of an item. An empty list means the item cannot be matched.</summary>
    /// <param name="item">The catalog item.</param>
    /// <returns>The distinct keys, in a stable order.</returns>
    public static IReadOnlyList<string> For(CatalogItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var keys = new List<string>();
        if (string.Equals(item.Type, "Movie", StringComparison.Ordinal))
        {
            AddKeys(keys, item.ProviderIds, MovieKinds, "movie", string.Empty);
        }
        else if (string.Equals(item.Type, "Episode", StringComparison.Ordinal)
            && item.SeasonNumber is { } season
            && item.EpisodeNumber is { } episode)
        {
            var suffix = string.Create(CultureInfo.InvariantCulture, $":s{season}e{episode}");
            AddKeys(keys, item.SeriesProviderIds, EpisodeKinds, "ep", suffix);
        }

        return keys;
    }

    /// <summary>
    /// Trims and lowercases a provider id and validates it against <c>^[a-z0-9_-]{1,32}$</c>. Provider ids come from
    /// untrusted peers and end up in keys and file names, so anything else is treated as absent.
    /// </summary>
    /// <param name="id">The raw provider id.</param>
    /// <returns>The normalized id, or <c>null</c> if it is blank or invalid.</returns>
    public static string? NormalizeId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var normalized = id.Trim().ToLowerInvariant();
        if (normalized.Length is 0 or > 32)
        {
            return null;
        }

        foreach (var c in normalized)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-'))
            {
                return null;
            }
        }

        return normalized;
    }

    /// <summary>Returns the provider kind (<c>tmdb</c>, <c>imdb</c>, <c>tvdb</c>) of a key.</summary>
    /// <param name="key">A key produced by <see cref="For"/>.</param>
    /// <returns>The provider kind, or an empty string if the key is malformed.</returns>
    internal static string KindOf(string key)
    {
        var first = key.IndexOf(':', StringComparison.Ordinal);
        if (first < 0)
        {
            return string.Empty;
        }

        var second = key.IndexOf(':', first + 1);
        return second < 0 ? string.Empty : key[(first + 1)..second];
    }

    private static void AddKeys(
        List<string> keys,
        Dictionary<string, string>? providerIds,
        string[] kinds,
        string prefix,
        string suffix)
    {
        if (providerIds is null)
        {
            return;
        }

        foreach (var kind in kinds)
        {
            var id = Lookup(providerIds, kind);
            if (id is not null)
            {
                keys.Add($"{prefix}:{kind}:{id}{suffix}");
            }
        }
    }

    private static string? Lookup(Dictionary<string, string> providerIds, string kind)
    {
        foreach (var (name, value) in providerIds)
        {
            if (string.Equals(name, kind, StringComparison.OrdinalIgnoreCase) && NormalizeId(value) is { } id)
            {
                return id;
            }
        }

        return null;
    }
}
