using JellyShare.Domain.Models.Placeholders;

namespace JellyShare.Domain.Interfaces.Placeholders;

/// <summary>
/// The library side of audio placeholders: Jellyfin reads no artist, album or track number from a <c>.strm</c> file, so
/// the plugin writes them on the items after the scan that created them.
/// </summary>
public interface IPlaceholderMetadata
{
    /// <summary>Calls <paramref name="handler"/> when the library adds an audio item under a placeholder root. Never blocks.</summary>
    /// <param name="handler">The callback.</param>
    /// <returns>Disposing unsubscribes.</returns>
    IDisposable SubscribeAdded(Action handler);

    /// <summary>
    /// Reads the audio placeholder items (relay <c>.strm</c> files) under the given roots, and the owner's own artist
    /// names (so placeholders use the owner's spelling).
    /// </summary>
    /// <param name="roots">The placeholder roots.</param>
    /// <returns>The items with their key and current metadata, and the owner's artist names.</returns>
    PlaceholderAudio ReadTracks(IReadOnlyList<string> roots);

    /// <summary>Writes the metadata on the items and locks it against Jellyfin's probe, then groups their artists and albums.</summary>
    /// <param name="tracks">The items with the metadata to write.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task WriteAsync(IReadOnlyList<PlaceholderTrack> tracks, CancellationToken ct);
}
