using Jellyfin.Plugin.JellyShare.Configuration;
using Jellyfin.Plugin.JellyShare.Protocol;
using Jellyfin.Plugin.JellyShare.Provider;
using Xunit;

namespace Jellyfin.Plugin.JellyShare.Tests.Provider;

internal sealed class FakeIdentity : IServerIdentity
{
    public string ServerName => "Alice";
}

internal sealed class FakeConfig : IConfigProvider
{
    public const string MoviesRoot = "/data/placeholders/Movies";
    public const string ShowsRoot = "/data/placeholders/Shows";

    public PluginConfiguration Current { get; } = new()
    {
        MoviesPlaceholderPath = MoviesRoot,
        ShowsPlaceholderPath = ShowsRoot,
    };
}

internal sealed class FakeEncoderSettings : IEncoderSettingsSource
{
    public EncoderSettings? Settings { get; set; } = new("/usr/lib/jellyfin-ffmpeg/ffmpeg", HardwareEncoder.None, null);

    public EncoderSettings? Get() => Settings;
}

internal sealed class FakeFfmpegRunner : IFfmpegRunner
{
    public static readonly byte[] Payload = System.Text.Encoding.ASCII.GetBytes("G-TS-PAYLOAD");

    public List<(string Exe, IReadOnlyList<string> Args)> Calls { get; } = new();

    public bool BlockUntilCancelled { get; set; }

    public bool FailFirstCallWithoutOutput { get; set; }

    public bool FailHardwareWithoutOutput { get; set; }

    public bool FailAlwaysWithoutOutput { get; set; }

    public bool ThrowStartFailure { get; set; }

    public bool SawCancellation => Cancellations > 0;

    public int Cancellations => Volatile.Read(ref _cancellations);

    public int MaxRunning => Volatile.Read(ref _maxRunning);

    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly SemaphoreSlim _starts = new(0);
    private int _running;
    private int _maxRunning;
    private int _cancellations;

    public async Task WaitForStartsAsync(int count)
    {
        for (var i = 0; i < count; i++)
        {
            Assert.True(await _starts.WaitAsync(TimeSpan.FromSeconds(5)), "runner did not start");
        }
    }

    public async Task<FfmpegRunResult> RunAsync(string executable, IReadOnlyList<string> arguments, Stream output, CancellationToken cancellationToken)
    {
        int callNumber;
        lock (Calls)
        {
            Calls.Add((executable, arguments.ToList()));
            callNumber = Calls.Count;
        }

        if (ThrowStartFailure)
        {
            throw new System.ComponentModel.Win32Exception(2, "No such file or directory");
        }

        var running = Interlocked.Increment(ref _running);
        InterlockedMax(ref _maxRunning, running);
        try
        {
            Started.TrySetResult();
            _starts.Release();
            if ((FailFirstCallWithoutOutput && callNumber == 1)
                || FailAlwaysWithoutOutput
                || (FailHardwareWithoutOutput && !arguments.Contains("libx264")))
            {
                return new FfmpegRunResult(1, 0, "Unknown encoder");
            }

            await output.WriteAsync(Payload, cancellationToken);
            if (BlockUntilCancelled)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref _cancellations);
                    throw;
                }
            }

            return new FfmpegRunResult(0, Payload.Length, string.Empty);
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}

/// <summary>Request lifetime that cancels <c>RequestAborted</c> on <c>Abort()</c>, as Kestrel does.</summary>
internal sealed class RecordingLifetime : Microsoft.AspNetCore.Http.Features.IHttpRequestLifetimeFeature
{
    private readonly CancellationTokenSource _cts;

    public RecordingLifetime(CancellationToken shared, CancellationToken extra)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(shared, extra);
        RequestAborted = _cts.Token;
    }

    public bool Aborted { get; private set; }

    public CancellationToken RequestAborted { get; set; }

    public void Abort()
    {
        Aborted = true;
        _cts.Cancel();
    }
}

internal sealed class LevelLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Entries)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}

internal static class FakeCapped
{
    public static CappedStreamService Create()
        => new(new FakeEncoderSettings(), new FakeFfmpegRunner(), TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<CappedStreamService>.Instance);
}

/// <summary>Response feature that records OnCompleted callbacks so tests can finish a response.</summary>
internal sealed class CompletableResponseFeature : Microsoft.AspNetCore.Http.Features.HttpResponseFeature
{
    private readonly List<(Func<object, Task> Callback, object State)> _completed = new();

    public override void OnCompleted(Func<object, Task> callback, object state) => _completed.Add((callback, state));

    public async Task CompleteAsync()
    {
        var callbacks = _completed.ToList();
        _completed.Clear();
        foreach (var (callback, state) in callbacks)
        {
            await callback(state);
        }
    }
}

internal sealed class FakeSource : ILocalItemSource
{
    public List<(Guid Library, LocalItem Item)> Items { get; } = new();

    public Dictionary<(Guid, string), string> Images { get; } = new();

    public TimeSpan GetItemsDelay { get; set; }

    public int GetItemsCalls => _calls;

    private int _calls;

    public IEnumerable<LocalItem> GetItems(IReadOnlyCollection<Guid> libraryIds)
    {
        Interlocked.Increment(ref _calls);
        if (GetItemsDelay > TimeSpan.Zero)
        {
            Thread.Sleep(GetItemsDelay);
        }

        return Items.Where(i => libraryIds.Contains(i.Library)).Select(i => i.Item).ToList();
    }

    public int CandidateCalls => _candidateCalls;

    private int _candidateCalls;

    public IEnumerable<LocalItem> GetExposureCandidates(Guid itemId, IReadOnlyCollection<Guid> libraryIds)
    {
        Interlocked.Increment(ref _candidateCalls);
        return Items
            .Where(i => libraryIds.Contains(i.Library) && (i.Item.Id == itemId || i.Item.SeriesId == itemId))
            .Select(i => i.Item)
            .ToList();
    }

    public string? GetImagePath(Guid itemId, string imageType)
        => Images.TryGetValue((itemId, imageType), out var p) ? p : null;

    public Dictionary<Guid, StreamInfo> Streams { get; } = new();

    public StreamInfo? GetStreamInfo(Guid itemId)
    {
        if (Streams.TryGetValue(itemId, out var info))
        {
            return info;
        }

        return Items.Any(i => i.Item.SeriesId == itemId) ? new StreamInfo("Series", "series", null, null) : null;
    }
}
