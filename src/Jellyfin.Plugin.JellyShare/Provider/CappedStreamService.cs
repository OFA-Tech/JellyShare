using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyShare.Provider;

/// <summary>Video encoder used for capped streams.</summary>
public enum HardwareEncoder
{
    /// <summary>Software libx264.</summary>
    None,

    /// <summary>NVIDIA NVENC.</summary>
    Nvenc,

    /// <summary>Intel Quick Sync.</summary>
    Qsv,

    /// <summary>VA-API.</summary>
    Vaapi,

    /// <summary>Apple VideoToolbox.</summary>
    VideoToolbox,

    /// <summary>AMD AMF.</summary>
    Amf,
}

/// <summary>The ffmpeg binary and hardware encoder configured in Jellyfin.</summary>
/// <param name="FfmpegPath">Path of Jellyfin's ffmpeg.</param>
/// <param name="Hardware">The hardware encoder, or <see cref="HardwareEncoder.None"/>.</param>
/// <param name="VaapiDevice">The VA-API render node, when <paramref name="Hardware"/> is VA-API.</param>
public sealed record EncoderSettings(string FfmpegPath, HardwareEncoder Hardware, string? VaapiDevice);

/// <summary>Reads the current encoder settings.</summary>
public interface IEncoderSettingsSource
{
    /// <summary>Gets the current settings.</summary>
    /// <returns>The settings, or <c>null</c> when Jellyfin has no usable ffmpeg.</returns>
    EncoderSettings? Get();
}

/// <summary>The outcome of an ffmpeg run.</summary>
/// <param name="ExitCode">The process exit code.</param>
/// <param name="BytesWritten">Bytes copied from stdout to the output.</param>
/// <param name="ErrorTail">The last part of stderr.</param>
public sealed record FfmpegRunResult(int ExitCode, long BytesWritten, string ErrorTail);

/// <summary>Runs ffmpeg and copies its stdout to a stream.</summary>
public interface IFfmpegRunner
{
    /// <summary>Runs the executable until it exits or <paramref name="cancellationToken"/> fires (then it is killed).</summary>
    /// <param name="executable">The executable.</param>
    /// <param name="arguments">The arguments, passed without a shell.</param>
    /// <param name="output">Where stdout is copied.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>The run result.</returns>
    Task<FfmpegRunResult> RunAsync(string executable, IReadOnlyList<string> arguments, Stream output, CancellationToken cancellationToken);
}

/// <summary>Serves live transcodes within a share's bitrate cap.</summary>
public sealed class CappedStreamService
{
    private readonly IEncoderSettingsSource _settings;
    private readonly IFfmpegRunner _runner;
    private readonly ILogger<CappedStreamService> _logger;

    /// <summary>Initializes a new instance of the <see cref="CappedStreamService"/> class.</summary>
    /// <param name="settings">The encoder settings.</param>
    /// <param name="runner">The ffmpeg runner.</param>
    /// <param name="logger">The logger.</param>
    public CappedStreamService(IEncoderSettingsSource settings, IFfmpegRunner runner, ILogger<CappedStreamService> logger)
    {
        _settings = settings;
        _runner = runner;
        _logger = logger;
    }

    /// <summary>Gets the settings to transcode within <paramref name="cap"/>, or <c>null</c> when that is not possible.</summary>
    /// <param name="cap">The cap in bits per second.</param>
    /// <returns>The settings, or <c>null</c>.</returns>
    public EncoderSettings? Prepare(long cap)
    {
        if (!CappedStreamArgs.CanCap(cap))
        {
            return null;
        }

        var settings = _settings.Get();
        return settings is null || string.IsNullOrWhiteSpace(settings.FfmpegPath) ? null : settings;
    }

    /// <summary>Creates the result that streams the transcode.</summary>
    /// <param name="settings">Settings from <see cref="Prepare"/>.</param>
    /// <param name="inputPath">The item's file (from Jellyfin, never from the request).</param>
    /// <param name="cap">The cap in bits per second.</param>
    /// <param name="startSeconds">Where to start, or <c>null</c>.</param>
    /// <param name="lease">The stream session, released when the transcode ends.</param>
    /// <returns>The action result.</returns>
    public IActionResult CreateResult(EncoderSettings settings, string inputPath, long cap, double? startSeconds, StreamLease lease)
        => new CappedStreamResult(this, settings, inputPath, cap, startSeconds, lease);

    private async Task TranscodeAsync(EncoderSettings settings, string inputPath, long cap, double? startSeconds, Stream output, CancellationToken cancellationToken)
    {
        var args = CappedStreamArgs.Build(inputPath, cap, startSeconds, settings.Hardware, settings.VaapiDevice);
        var result = await _runner.RunAsync(settings.FfmpegPath, args, output, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 && result.BytesWritten == 0 && settings.Hardware != HardwareEncoder.None)
        {
            _logger.LogDebug("Hardware encoder {Encoder} failed before output (exit {Code}), retrying in software: {Error}", settings.Hardware, result.ExitCode, result.ErrorTail);
            args = CappedStreamArgs.Build(inputPath, cap, startSeconds, HardwareEncoder.None, null);
            result = await _runner.RunAsync(settings.FfmpegPath, args, output, cancellationToken).ConfigureAwait(false);
        }

        if (result.ExitCode != 0)
        {
            _logger.LogDebug("Capped stream ffmpeg exited with {Code} after {Bytes} bytes: {Error}", result.ExitCode, result.BytesWritten, result.ErrorTail);
        }
    }

    private sealed class CappedStreamResult : IActionResult
    {
        private readonly CappedStreamService _service;
        private readonly EncoderSettings _settings;
        private readonly string _inputPath;
        private readonly long _cap;
        private readonly double? _start;
        private readonly StreamLease _lease;

        public CappedStreamResult(CappedStreamService service, EncoderSettings settings, string inputPath, long cap, double? start, StreamLease lease)
        {
            _service = service;
            _settings = settings;
            _inputPath = inputPath;
            _cap = cap;
            _start = start;
            _lease = lease;
        }

        public async Task ExecuteResultAsync(ActionContext context)
        {
            var http = context.HttpContext;
            try
            {
                var response = http.Response;
                response.StatusCode = StatusCodes.Status200OK;
                response.ContentType = "video/mp2t";
                response.Headers.AcceptRanges = "none";
                response.Headers.CacheControl = "no-store";
                if (HttpMethods.IsHead(http.Request.Method))
                {
                    return;
                }

                await _service.TranscodeAsync(_settings, _inputPath, _cap, _start, response.Body, http.RequestAborted).ConfigureAwait(false);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                _service._logger.LogWarning("Could not start ffmpeg for a capped stream: {Error}", ex.Message);
                if (!http.Response.HasStarted)
                {
                    http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    http.Response.Headers.Remove("Accept-Ranges");
                    http.Response.ContentType = null;
                }
            }
            catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
            {
                _service._logger.LogDebug("Capped stream client disconnected");
            }
            catch (IOException ex)
            {
                // The client went away while a chunk was written.
                _service._logger.LogDebug(ex, "Capped stream write failed");
            }
            finally
            {
                _lease.Dispose();
            }
        }
    }
}
