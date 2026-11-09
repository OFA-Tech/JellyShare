using JellyShare.Domain.Keys;

namespace JellyShare.Application.Features.Placeholders;

/// <summary>
/// The owner's spelling of artist names. Jellyfin merges artists only by (case-insensitive) name, so a friend's
/// "Beyonce" next to the owner's "Beyoncé" would be a second artist: placeholders use the owner's spelling instead.
/// </summary>
public static class OwnerArtists
{
    /// <summary>Gets the owner's names keyed by <see cref="KeyText.Normalize"/>; of two spellings the ordinal-first wins (stable).</summary>
    /// <param name="names">The artist names of the owner's own tracks.</param>
    /// <returns>Normalized name to the owner's spelling.</returns>
    public static IReadOnlyDictionary<string, string> Build(IEnumerable<string?> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var key = KeyText.Normalize(name);
            if (key.Length > 0 && (!map.TryGetValue(key, out var known) || string.CompareOrdinal(name, known) < 0))
            {
                map[key] = name!;
            }
        }

        return map;
    }

    /// <summary>Gets the owner's spelling of a name, or the name itself when the owner has no such artist.</summary>
    /// <param name="owner">The owner's names from <see cref="Build"/>, or <c>null</c>.</param>
    /// <param name="name">The friend's name.</param>
    /// <returns>The name to use.</returns>
    public static string Resolve(IReadOnlyDictionary<string, string>? owner, string name)
        => owner is not null && owner.TryGetValue(KeyText.Normalize(name), out var own) ? own : name;
}
