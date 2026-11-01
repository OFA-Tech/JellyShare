using JellyShare.Domain.Interfaces.Library;
using MediaBrowser.Controller.Library;

namespace JellyShare.Jellyfin.Adapters;

/// <summary><see cref="IScanReporter"/> using <see cref="ILibraryMonitor.ReportFileSystemChanged"/>.</summary>
public sealed class LibraryMonitorScanReporter : IScanReporter
{
    private readonly ILibraryMonitor _monitor;

    /// <summary>Initializes a new instance of the <see cref="LibraryMonitorScanReporter"/> class.</summary>
    /// <param name="monitor">The library monitor.</param>
    public LibraryMonitorScanReporter(ILibraryMonitor monitor)
    {
        _monitor = monitor;
    }

    /// <inheritdoc />
    public void ReportChanged(string fullPath) => _monitor.ReportFileSystemChanged(fullPath);
}
