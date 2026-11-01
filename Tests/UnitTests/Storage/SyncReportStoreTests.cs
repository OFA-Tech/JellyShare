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

        loaded.ShouldNotBeNull();
        loaded!.Offered.ShouldBe(report.Offered);
        loaded.Unmatched.ShouldBe(report.Unmatched);
        loaded.SkippedLocal.ShouldBe(report.SkippedLocal);
        loaded.Written.ShouldBe(report.Written);
        loaded.Deleted.ShouldBe(report.Deleted);
        loaded.PeerErrors.ShouldBe(report.PeerErrors);
        loaded.Skipped.ShouldBe(report.Skipped);
        loaded.Conflicts.ShouldBe(report.Conflicts);
        loaded.Scanned.ShouldBe(report.Scanned);
        loaded.PendingScans.ShouldBe(report.PendingScans);
        loaded.ImagesWritten.ShouldBe(report.ImagesWritten);
        loaded.UncheckedLocalFiles.ShouldBe(report.UncheckedLocalFiles);
        loaded.MissingLocalFiles.ShouldBe(report.MissingLocalFiles);
        loaded.FinishedUtc.ShouldBe(report.FinishedUtc);
    }

    [Fact]
    public void Load_MissingOrCorruptFile_IsNull()
    {
        SyncReportStore.Load(_tmp.Sub("data")).ShouldBeNull();

        Directory.CreateDirectory(_tmp.Sub("data"));
        File.WriteAllText(Path.Combine(_tmp.Sub("data"), SyncReportStore.FileName), "{ not json");

        SyncReportStore.Load(_tmp.Sub("data")).ShouldBeNull();
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
        first.LastReport.ShouldBeNull();
        var report = await first.RunAsync(CancellationToken.None);

        var restarted = Create();

        restarted.LastReport.ShouldNotBeNull();
        restarted.LastReport!.Offered.ShouldBe(report.Offered);
        restarted.LastReport.FinishedUtc.ShouldBe(report.FinishedUtc);
    }
}
