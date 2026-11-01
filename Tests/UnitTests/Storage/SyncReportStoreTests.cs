using System.Net;
using System.Text;
using JellyShare.Application.Features.Sync;
using JellyShare.Data.Files.Placeholders;
using JellyShare.Data.Files.Stores;
using JellyShare.Domain.Exceptions;
using JellyShare.Domain.Models.Placeholders;
using JellyShare.Domain.Models.Sync;
using JellyShare.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JellyShare.UnitTests.Storage;

public sealed class SyncReportStoreTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void SaveThenLoad_RoundTripsAllFields()
    {
        var report = new SyncReport(10, 2, 3, 4, 5, ["bob: HTTP 401"])
        {
            Skipped = 1,
            Conflicts = 2,
            Scanned = 3,
            PendingScans = 4,
            ImagesWritten = 5,
            UncheckedLocalFiles = 6,
            MissingLocalFiles = 7,
            FinishedUtc = new DateTimeOffset(2026, 10, 31, 12, 30, 0, TimeSpan.Zero),
        };

        SyncReportStore.Save(_tmp.Sub("data"), report);
        var loaded = SyncReportStore.Load(_tmp.Sub("data"));

        Assert.NotNull(loaded);
        Assert.Equal(report.Offered, loaded!.Offered);
        Assert.Equal(report.Unmatched, loaded.Unmatched);
        Assert.Equal(report.SkippedLocal, loaded.SkippedLocal);
        Assert.Equal(report.Written, loaded.Written);
        Assert.Equal(report.Deleted, loaded.Deleted);
        Assert.Equal(report.PeerErrors, loaded.PeerErrors);
        Assert.Equal(report.Skipped, loaded.Skipped);
        Assert.Equal(report.Conflicts, loaded.Conflicts);
        Assert.Equal(report.Scanned, loaded.Scanned);
        Assert.Equal(report.PendingScans, loaded.PendingScans);
        Assert.Equal(report.ImagesWritten, loaded.ImagesWritten);
        Assert.Equal(report.UncheckedLocalFiles, loaded.UncheckedLocalFiles);
        Assert.Equal(report.MissingLocalFiles, loaded.MissingLocalFiles);
        Assert.Equal(report.FinishedUtc, loaded.FinishedUtc);
    }

    [Fact]
    public void Load_MissingOrCorruptFile_IsNull()
    {
        Assert.Null(SyncReportStore.Load(_tmp.Sub("data")));

        Directory.CreateDirectory(_tmp.Sub("data"));
        File.WriteAllText(Path.Combine(_tmp.Sub("data"), SyncReportStore.FileName), "{ not json");

        Assert.Null(SyncReportStore.Load(_tmp.Sub("data")));
    }

    [Fact]
    public async Task SyncService_PersistsLastReport_SoANewInstanceSeesItAfterRestart()
    {
        var config = new ConsumerConfig();
        config.Settings.MoviesPlaceholderPath = _tmp.Sub("ph", "Movies");
        config.Settings.ShowsPlaceholderPath = _tmp.Sub("ph", "Shows");
        SyncService Create() => TestSync.Create(
            config,
            new FakePeerClient(),
            new LocalIndex(new FakeLocalLibrary(), config, FakeLocalLibrary.Exists(_ => true)),
            new FakeScans(),
            new FixedRelay(),
            () => _tmp.Sub("data"),
            new MutableClock(),
            NullLogger<SyncService>.Instance);

        var first = Create();
        Assert.Null(first.LastReport);
        var report = await first.RunAsync(CancellationToken.None);

        var restarted = Create();

        Assert.NotNull(restarted.LastReport);
        Assert.Equal(report.Offered, restarted.LastReport!.Offered);
        Assert.Equal(report.FinishedUtc, restarted.LastReport.FinishedUtc);
    }
}
