using JellyShare.Domain.Enums;
using JellyShare.Domain.Interfaces.Streaming;
using JellyShare.Domain.Models.Streaming;
using Microsoft.Extensions.Logging;

namespace JellyShare.Application.Features.Streaming;

/// <summary>A reserved transcode of one stream session; it is cancelled when it is the oldest of a full session.</summary>
public sealed class TranscodeSlot : IDisposable
{
    private readonly CappedStreamService _owner;
    private readonly CancellationTokenSource _cts = new();
    private int _released;

    internal TranscodeSlot(CappedStreamService owner, (Guid Share, Guid Item) key)
    {
        _owner = owner;
        Key = key;
    }

    /// <summary>Gets a token that is cancelled when a newer request for the same session takes over.</summary>
    public CancellationToken Superseded => _cts.Token;

    internal (Guid Share, Guid Item) Key { get; }

    /// <summary>Frees the slot.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _owner.Release(this);
            _cts.Dispose();
        }
    }

    internal void Supersede() => _cts.Cancel();
}

/// <summary>
/// Serves live transcodes within a share's bitrate cap. At most <see cref="MaxTranscodesPerSession"/> run per stream
/// session (a further request cancels the oldest, which also covers a seek restart), and at most a configured number
/// run across all shares.
/// </summary>
public sealed class CappedStreamService
{
    /// <summary>How long hardware encoding is skipped after it failed where software worked.</summary>
    public static readonly TimeSpan HardwareRetryAfter = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Transcodes one stream session (share + title) may run at once: two consumer users can watch the same title,
    /// and a further request (e.g. a seek restart) cancels the oldest.
    /// </summary>
    public const int MaxTranscodesPerSession = 2;

    private readonly IEncoderSettingsSource _settings;
    private readonly IFfmpegRunner _runner;
    private readonly TimeProvider _clock;
    private readonly ILogger<CappedStreamService> _logger;
    private readonly object _lock = new();
    private readonly Dictionary<(Guid Share, Guid Item), List<TranscodeSlot>> _running = new();
    private int _total;
    private DateTimeOffset _hardwareOffUntil = DateTimeOffset.MinValue;

    /// <summary>Initializes a new instance of the <see cref="CappedStreamService"/> class.</summary>
    /// <param name="settings">The encoder settings.</param>
    /// <param name="runner">The ffmpeg runner.</param>
    /// <param name="clock">The clock (for the hardware back-off).</param>
    /// <param name="logger">The logger.</param>
    public CappedStreamService(IEncoderSettingsSource settings, IFfmpegRunner runner, TimeProvider clock, ILogger<CappedStreamService> logger)
    {
        _settings = settings;
        _runner = runner;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Gets the number of reserved (running) transcodes.</summary>
    internal int RunningTranscodes
    {
        get
        {
            lock (_lock)
            {
                return _total;
            }
        }
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

    /// <summary>
    /// Reserves a transcode of a session. When the session already runs <see cref="MaxTranscodesPerSession"/>, its
    /// oldest transcode is cancelled and replaced (no new place is used); otherwise the new one needs a free place
    /// under <paramref name="maxConcurrent"/>.
    /// </summary>
    /// <param name="shareId">The share.</param>
    /// <param name="itemId">The item.</param>
    /// <param name="maxConcurrent">The server-wide transcode limit.</param>
    /// <returns>The slot, or <c>null</c> when the limit is reached.</returns>
    public TranscodeSlot? TryReserve(Guid shareId, Guid itemId, int maxConcurrent)
    {
        var key = (shareId, itemId);
        lock (_lock)
        {
            if (!_running.TryGetValue(key, out var session))
            {
                session = new List<TranscodeSlot>(MaxTranscodesPerSession);
            }

            if (session.Count >= MaxTranscodesPerSession)
            {
                var oldest = session[0];
                session.RemoveAt(0);
                oldest.Supersede();
            }
            else if (_total >= maxConcurrent)
            {
                return null;
            }
            else
            {
                _total++;
            }

            var slot = new TranscodeSlot(this, key);
            session.Add(slot);
            _running[key] = session;
            return slot;
        }
    }

    /// <summary>
    /// Runs the transcode of a reserved slot into <paramref name="output"/> until it ends, the caller goes away
    /// (<paramref name="requestAborted"/>) or a newer request for the same session takes over (the slot is
    /// superseded). Disposes neither the slot nor the stream session; the caller does that when the response ends.
    /// </summary>
    /// <param name="settings">Settings from <see cref="Prepare"/>.</param>
    /// <param name="inputPath">The item's file (from the library, never from the request).</param>
    /// <param name="cap">The cap in bits per second.</param>
    /// <param name="startSeconds">Where to start, or <c>null</c>.</param>
    /// <param name="slot">The reserved transcode.</param>
    /// <param name="output">Where the MPEG-TS goes (the response body).</param>
    /// <param name="requestAborted">Cancelled when the client disconnects.</param>
    /// <returns>How the stream ended; the caller maps it to the response.</returns>
    public async Task<CappedStreamOutcome> StreamAsync(
        EncoderSettings settings,
        string inputPath,
        long cap,
        double? startSeconds,
        TranscodeSlot slot,
        Stream output,
        CancellationToken requestAborted)
    {
        ArgumentNullException.ThrowIfNull(slot);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, slot.Superseded);
        try
        {
            var result = await TranscodeAsync(settings, inputPath, cap, startSeconds, output, cancel.Token).ConfigureAwait(false);
            return result.ExitCode != 0 && result.BytesWritten == 0 ? CappedStreamOutcome.FailedWithoutOutput : CappedStreamOutcome.Completed;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _logger.LogWarning(ex, "Could not start ffmpeg for a capped stream: {Error}", ex.Message);
            return CappedStreamOutcome.FailedWithoutOutput;
        }
        catch (OperationCanceledException ex) when (cancel.IsCancellationRequested)
        {
            if (!requestAborted.IsCancellationRequested)
            {
                // A newer request for the same session took over: the caller cuts this response so the client does
                // not take the end of the body for the end of the title.
                _logger.LogDebug(ex, "Capped stream replaced by a newer request for the same title");
                return CappedStreamOutcome.Superseded;
            }

            _logger.LogDebug(ex, "Capped stream client disconnected");
            return CappedStreamOutcome.ClientDisconnected;
        }
        catch (IOException ex)
        {
            // The client went away while a chunk was written.
            _logger.LogDebug(ex, "Capped stream write failed");
            return CappedStreamOutcome.ClientDisconnected;
        }
    }

    internal void Release(TranscodeSlot slot)
    {
        lock (_lock)
        {
            // A superseded slot was already taken out of its session (and the total) when it was replaced.
            if (_running.TryGetValue(slot.Key, out var session) && session.Remove(slot))
            {
                _total--;
                if (session.Count == 0)
                {
                    _running.Remove(slot.Key);
                }
            }
        }
    }

    private async Task<FfmpegRunResult> TranscodeAsync(EncoderSettings settings, string inputPath, long cap, double? startSeconds, Stream output, CancellationToken cancellationToken)
    {
        var hardware = settings.Hardware;
        if (hardware != HardwareEncoder.None && !HardwareAllowed())
        {
            hardware = HardwareEncoder.None;
        }

        var args = CappedStreamArgs.Build(inputPath, cap, startSeconds, hardware, settings.VaapiDevice);
        var result = await _runner.RunAsync(settings.FfmpegPath, args, output, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 && result.BytesWritten == 0 && hardware != HardwareEncoder.None)
        {
            args = CappedStreamArgs.Build(inputPath, cap, startSeconds, HardwareEncoder.None, null);
            var software = await _runner.RunAsync(settings.FfmpegPath, args, output, cancellationToken).ConfigureAwait(false);
            if (software.BytesWritten > 0)
            {
                DisableHardware(hardware, result);
            }

            result = software;
        }

        if (result.ExitCode != 0)
        {
            _logger.LogDebug("Capped stream ffmpeg exited with {Code} after {Bytes} bytes: {Error}", result.ExitCode, result.BytesWritten, result.ErrorTail);
        }

        return result;
    }

    private bool HardwareAllowed()
    {
        var now = _clock.GetUtcNow();
        lock (_lock)
        {
            return now >= _hardwareOffUntil;
        }
    }

    private void DisableHardware(HardwareEncoder hardware, FfmpegRunResult failure)
    {
        var now = _clock.GetUtcNow();
        bool first;
        lock (_lock)
        {
            first = now >= _hardwareOffUntil;
            _hardwareOffUntil = now + HardwareRetryAfter;
        }

        if (first)
        {
            _logger.LogWarning(
                "Hardware encoder {Encoder} failed for a capped stream (exit {Code}); using software x264 for {Minutes} minutes. ffmpeg said: {Error}",
                hardware,
                failure.ExitCode,
                HardwareRetryAfter.TotalMinutes,
                failure.ErrorTail);
        }
    }
}
