using JellyShare.Application.Features.Streaming;
using JellyShare.Domain.Enums;
using JellyShare.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JellyShare.UnitTests.Streaming;

/// <summary>The plain stream API the HTTP result in JellyShare.Jellyfin maps to a response.</summary>
public sealed class CappedStreamServiceTests
{
    private static readonly Guid Share = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid Item = Guid.Parse("00000001-0000-0000-0000-000000000001");

    private readonly FakeFfmpegRunner _runner = new();
    private readonly CappedStreamService _service;

    public CappedStreamServiceTests()
    {
        _service = new CappedStreamService(new FakeEncoderSettings(), _runner, TimeProvider.System, NullLogger<CappedStreamService>.Instance);
    }

    private Task<CappedStreamOutcome> Run(TranscodeSlot slot, Stream output, CancellationToken ct = default)
        => _service.StreamAsync(_service.Prepare(4_000_000)!, "/media/movie.mkv", 4_000_000, null, slot, output, ct);

    [Fact]
    public async Task StreamAsync_WritesTheTranscode_AndCompletes()
    {
        using var slot = _service.TryReserve(Share, Item, 4)!;
        using var output = new MemoryStream();

        var outcome = await Run(slot, output);

        Assert.Equal(CappedStreamOutcome.Completed, outcome);
        Assert.Equal(FakeFfmpegRunner.Payload, output.ToArray());
    }

    [Fact]
    public async Task StreamAsync_FailureWithoutOutput_IsReported()
    {
        _runner.FailAlwaysWithoutOutput = true;
        using var slot = _service.TryReserve(Share, Item, 4)!;

        Assert.Equal(CappedStreamOutcome.FailedWithoutOutput, await Run(slot, new MemoryStream()));
    }

    [Fact]
    public async Task StreamAsync_FfmpegCannotStart_IsAFailureWithoutOutput()
    {
        _runner.ThrowStartFailure = true;
        using var slot = _service.TryReserve(Share, Item, 4)!;

        Assert.Equal(CappedStreamOutcome.FailedWithoutOutput, await Run(slot, new MemoryStream()));
    }

    [Fact]
    public async Task StreamAsync_ClientDisconnect_IsReported()
    {
        _runner.BlockUntilCancelled = true;
        using var slot = _service.TryReserve(Share, Item, 4)!;
        using var aborted = new CancellationTokenSource();

        var run = Run(slot, new MemoryStream(), aborted.Token);
        await _runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await aborted.CancelAsync();

        Assert.Equal(CappedStreamOutcome.ClientDisconnected, await run);
    }

    [Fact]
    public async Task StreamAsync_NewerRequestsForTheSession_SupersedeTheOldest()
    {
        _runner.BlockUntilCancelled = true;
        using var first = _service.TryReserve(Share, Item, 4)!;
        var run = Run(first, new MemoryStream());
        await _runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var second = _service.TryReserve(Share, Item, 4)!;
        using var third = _service.TryReserve(Share, Item, 4)!;

        Assert.Equal(CappedStreamOutcome.Superseded, await run);
    }
}
