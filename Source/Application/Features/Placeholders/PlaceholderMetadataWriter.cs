using System.Diagnostics.CodeAnalysis;
using JellyShare.Domain.Interfaces.Configuration;
using JellyShare.Domain.Interfaces.Placeholders;
using JellyShare.Domain.Interfaces.Sync;
using JellyShare.Domain.Models.Placeholders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JellyShare.Application.Features.Placeholders;

/// <summary>
/// Writes track metadata on audio placeholder items: once after startup and after each scan that added audio
/// placeholders (debounced). Only items whose values differ are written, so its own updates never trigger it again.
/// </summary>
public sealed class PlaceholderMetadataWriter : IHostedService, IDisposable
{
    /// <summary>Quiet time after the last added placeholder (and after startup) before a pass runs.</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(10);

    private readonly IPlaceholderMetadata _metadata;
    private readonly IConsumerIndex _index;
    private readonly ISettingsProvider _config;
    private readonly TimeProvider _clock;
    private readonly ILogger<PlaceholderMetadataWriter> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    [SuppressMessage("Major Bug", "S2930:IDisposables should be disposed", Justification = "Cancelled, never disposed: a timer callback racing with Dispose may still read its token.")]
    private readonly CancellationTokenSource _cts = new();
    private IDisposable? _subscription;
    private ITimer? _timer;

    /// <summary>Initializes a new instance of the <see cref="PlaceholderMetadataWriter"/> class.</summary>
    /// <param name="metadata">The library side of the placeholders.</param>
    /// <param name="index">The merged index (what each placeholder is).</param>
    /// <param name="config">The settings (placeholder roots).</param>
    /// <param name="clock">The clock used to create the timer.</param>
    /// <param name="logger">The logger.</param>
    public PlaceholderMetadataWriter(
        IPlaceholderMetadata metadata,
        IConsumerIndex index,
        ISettingsProvider config,
        TimeProvider clock,
        ILogger<PlaceholderMetadataWriter> logger)
    {
        _metadata = metadata;
        _index = index;
        _config = config;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = _clock.CreateTimer(_ => Fire(), null, Delay, Timeout.InfiniteTimeSpan);
        _subscription = _metadata.SubscribeAdded(() => _timer?.Change(Delay, Timeout.InfiniteTimeSpan));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Interlocked.Exchange(ref _subscription, null)?.Dispose();
        Interlocked.Exchange(ref _timer, null)?.Dispose();
        _cts.Cancel();
    }

    /// <summary>Writes the metadata of every audio placeholder item whose values differ from its merged entry.</summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The items written.</returns>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cfg = _config.Current;
            var roots = new[] { cfg.MusicPlaceholderPath, cfg.AudiobooksPlaceholderPath }
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .ToList();
            if (roots.Count == 0)
            {
                return 0;
            }

            var writes = new List<PlaceholderTrack>();
            foreach (var track in _metadata.ReadTracks(roots))
            {
                if (_index.Find(track.Key) is { Sources.Count: > 0 } entry
                    && TrackMetadataRules.ToWrite(track.Metadata, TrackMetadataRules.Wanted(entry)) is { } wanted)
                {
                    writes.Add(track with { Metadata = wanted });
                }
            }

            if (writes.Count > 0)
            {
                await _metadata.WriteAsync(writes, ct).ConfigureAwait(false);
                _logger.LogInformation("JellyShare: wrote metadata on {Count} music/audiobook placeholders", writes.Count);
            }

            return writes.Count;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Fire()
    {
        var token = _cts.Token;
        if (token.IsCancellationRequested)
        {
            return;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await RunAsync(token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "JellyShare: writing placeholder metadata failed");
                }
            },
            CancellationToken.None);
    }
}
