using JellyShare.Domain.Models.Streaming;

namespace JellyShare.Domain.Interfaces.Streaming;

/// <summary>Reads the current encoder settings.</summary>
public interface IEncoderSettingsSource
{
    /// <summary>Gets the current settings.</summary>
    /// <returns>The settings, or <c>null</c> when Jellyfin has no usable ffmpeg.</returns>
    EncoderSettings? Get();
}
