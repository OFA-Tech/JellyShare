using JellyShare.Domain.Enums;

namespace JellyShare.Domain.Models.Streaming;

/// <summary>The ffmpeg binary and hardware encoder configured in Jellyfin.</summary>
/// <param name="FfmpegPath">Path of Jellyfin's ffmpeg.</param>
/// <param name="Hardware">The hardware encoder, or <see cref="HardwareEncoder.None"/>.</param>
/// <param name="VaapiDevice">The VA-API render node, when <paramref name="Hardware"/> is VA-API.</param>
public sealed record EncoderSettings(string FfmpegPath, HardwareEncoder Hardware, string? VaapiDevice);

/// <summary>The outcome of an ffmpeg run.</summary>
/// <param name="ExitCode">The process exit code.</param>
/// <param name="BytesWritten">Bytes copied from stdout to the output.</param>
/// <param name="ErrorTail">The last part of stderr.</param>
public sealed record FfmpegRunResult(int ExitCode, long BytesWritten, string ErrorTail);
