using JellyShare.Domain.Enums;
using JellyShare.Domain.Interfaces.Streaming;
using JellyShare.Domain.Models.Streaming;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;

namespace JellyShare.Jellyfin.Adapters;

/// <summary>Reads Jellyfin's ffmpeg path and hardware encoding settings (Dashboard → Playback → Transcoding).</summary>
public sealed class JellyfinEncoderSettings : IEncoderSettingsSource
{
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IConfigurationManager _configuration;

    /// <summary>Initializes a new instance of the <see cref="JellyfinEncoderSettings"/> class.</summary>
    /// <param name="mediaEncoder">Jellyfin's media encoder.</param>
    /// <param name="configuration">The server configuration.</param>
    public JellyfinEncoderSettings(IMediaEncoder mediaEncoder, IConfigurationManager configuration)
    {
        _mediaEncoder = mediaEncoder;
        _configuration = configuration;
    }

    /// <inheritdoc />
    public EncoderSettings? Get()
    {
        var path = _mediaEncoder.EncoderPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var options = _configuration.GetEncodingOptions();
        var hardware = !options.EnableHardwareEncoding
            ? HardwareEncoder.None
            : options.HardwareAccelerationType switch
            {
                HardwareAccelerationType.nvenc => HardwareEncoder.Nvenc,
                HardwareAccelerationType.qsv => HardwareEncoder.Qsv,
                HardwareAccelerationType.vaapi => HardwareEncoder.Vaapi,
                HardwareAccelerationType.videotoolbox => HardwareEncoder.VideoToolbox,
                HardwareAccelerationType.amf => HardwareEncoder.Amf,
                _ => HardwareEncoder.None,
            };
        return new EncoderSettings(path, hardware, options.VaapiDevice);
    }
}
