using MediaBrowser.Controller.Library;

namespace JellyShare.Jellyfin.Consumer;

/// <summary>Asks Jellyfin to rescan a changed path (targeted, never a full library refresh).</summary>
public interface IScanReporter
{
    /// <summary>Reports a changed directory.</summary>
    /// <param name="fullPath">The full directory path (may no longer exist).</param>
    void ReportChanged(string fullPath);
}

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
