using Jellyfin.Plugin.JellyShare.Configuration;
using Jellyfin.Plugin.JellyShare.Protocol;
using Jellyfin.Plugin.JellyShare.Provider;

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

    public bool SawCancellation { get; private set; }

    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<FfmpegRunResult> RunAsync(string executable, IReadOnlyList<string> arguments, Stream output, CancellationToken cancellationToken)
    {
        lock (Calls)
        {
            Calls.Add((executable, arguments.ToList()));
        }

        Started.TrySetResult();
        if (FailFirstCallWithoutOutput && Calls.Count == 1)
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
                SawCancellation = true;
                throw;
            }
        }

        return new FfmpegRunResult(0, Payload.Length, string.Empty);
    }
}

internal static class FakeCapped
{
    public static CappedStreamService Create()
        => new(new FakeEncoderSettings(), new FakeFfmpegRunner(), Microsoft.Extensions.Logging.Abstractions.NullLogger<CappedStreamService>.Instance);
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
