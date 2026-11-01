using JellyShare.Application.Features.Placeholders;
using JellyShare.Domain.Keys;
using JellyShare.Domain.Models.Library;

namespace JellyShare.Application.Features.Sync;

/// <summary>Builds the <see cref="LibraryScope"/> of a set of merged entries.</summary>
public static class LibraryScopes
{
    /// <summary>Builds the scope covering merged entries (their keys and, for episodes, all their series ids).</summary>
    /// <param name="entries">The entries.</param>
    /// <returns>The scope.</returns>
    public static LibraryScope ForEntries(IEnumerable<MergedEntry> entries)
    {
        var movies = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var series = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        static void Add(Dictionary<string, HashSet<string>> map, string kind, string id)
        {
            if (!map.TryGetValue(kind, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                map[kind] = set;
            }

            set.Add(id);
        }

        foreach (var entry in entries)
        {
            if (entry.Sources.Count == 0)
            {
                continue;
            }

            if (entry.Sources[0].Item.Type == "Episode")
            {
                foreach (var (kind, id) in PathNaming.SeriesIds(entry))
                {
                    Add(series, kind, id);
                }
            }
            else
            {
                foreach (var key in entry.Keys)
                {
                    // movie:{kind}:{id}
                    var parts = key.Split(':');
                    if (parts.Length == 3 && parts[0] == "movie")
                    {
                        Add(movies, parts[1], parts[2]);
                    }
                }
            }
        }

        return new LibraryScope(
            movies.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<string>)kv.Value, StringComparer.Ordinal),
            series.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<string>)kv.Value, StringComparer.Ordinal));
    }
}
