using System.Globalization;
using JellyShare.Domain.Models.Protocol;

namespace JellyShare.Domain.Keys;

/// <summary>
/// Derives the provider-id based identity keys used to merge the same title across peers.
/// </summary>
public static class MediaKey
{
    private static readonly string[] _movieKinds = ["tmdb", "imdb"];
    private static readonly string[] _episodeKinds = ["tvdb", "tmdb"];

    /// <summary>Returns all keys of an item. An empty list means the item cannot be matched.</summary>
    /// <param name="item">The catalog item.</param>
    /// <returns>The distinct keys, in a stable order.</returns>
    public static IReadOnlyList<string> For(CatalogItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var keys = new List<string>();
        if (string.Equals(item.Type, "Movie", StringComparison.Ordinal))
        {
            AddKeys(keys, item.ProviderIds, _movieKinds, "movie", string.Empty);
        }
        else if (string.Equals(item.Type, "Episode", StringComparison.Ordinal)
            && item.SeasonNumber is { } season
            && item.EpisodeNumber is { } episode)
        {
            var suffix = string.Create(CultureInfo.InvariantCulture, $":s{season}e{episode}");
            AddKeys(keys, item.SeriesProviderIds, _episodeKinds, "ep", suffix);
        }
        else if (string.Equals(item.Type, "Audio", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(item.Name))
        {
            if (RecordingId(item.ProviderIds) is { } mb)
            {
                keys.Add($"track:mb:{mb}");
            }

            var albumArtist = item.AlbumArtist ?? item.Artists?.FirstOrDefault();
            var disc = item.DiscNumber?.ToString(CultureInfo.InvariantCulture);
            var track = item.TrackNumber?.ToString(CultureInfo.InvariantCulture);
            keys.Add($"track:name:{KeyText.Hash(albumArtist, item.Album, disc, track, item.Name)}");
        }
        else if (string.Equals(item.Type, "AudioBook", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(item.Name))
        {
            keys.Add($"book:name:{KeyText.Hash(item.FolderPath, item.Name)}");
        }

        return keys;
    }

    // Jellyfin's file prober only fills MusicBrainzTrack (the release track id); the recording id comes from plugins.
    private static string? RecordingId(Dictionary<string, string>? providerIds)
        => providerIds is null ? null : Lookup(providerIds, "MusicBrainzRecording") ?? Lookup(providerIds, "MusicBrainzTrack");

    /// <summary>
    /// Trims and lowercases a provider id and validates it against <c>^[a-z0-9_-]{1,32}$</c> or, for a 36-character
    /// UUID (MusicBrainz), <c>^[0-9a-f-]{36}$</c>. Provider ids come from untrusted peers and end up in keys and
    /// file names, so anything else is treated as absent.
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
        return normalized.Length switch
        {
            0 => null,
            <= 32 => normalized.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-') ? normalized : null,
            36 => normalized.All(c => c is (>= 'a' and <= 'f') or (>= '0' and <= '9') or '-') ? normalized : null,
            _ => null,
        };
    }

    /// <summary>Returns the kind (<c>tmdb</c>, <c>imdb</c>, <c>tvdb</c>, <c>mb</c>, <c>name</c>) of a key.</summary>
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
