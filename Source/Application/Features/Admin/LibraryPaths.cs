namespace JellyShare.Application.Features.Admin;

/// <summary>Adding a folder to a library without leaving real-time monitoring off.</summary>
public static class LibraryPaths
{
    /// <summary>
    /// Stops the library monitor, adds the path and queues one library scan in the background; the queued scan
    /// starts the monitor again. When adding fails the monitor is started again and the error propagates; when
    /// queueing the scan fails it is reported and the monitor is started again.
    /// </summary>
    /// <param name="stopMonitor">Stops real-time monitoring.</param>
    /// <param name="startMonitor">Starts real-time monitoring.</param>
    /// <param name="addPath">Adds the folder to the library.</param>
    /// <param name="queueScan">Queues the library scan.</param>
    /// <param name="background">Runs work in the background.</param>
    /// <param name="onScanQueueFailure">Reports a failure to queue the scan.</param>
    public static void AddWithMonitorRecovery(
        Action stopMonitor,
        Action startMonitor,
        Action addPath,
        Action queueScan,
        Action<Action> background,
        Action<Exception> onScanQueueFailure)
    {
        ArgumentNullException.ThrowIfNull(stopMonitor);
        ArgumentNullException.ThrowIfNull(startMonitor);
        ArgumentNullException.ThrowIfNull(addPath);
        ArgumentNullException.ThrowIfNull(queueScan);
        ArgumentNullException.ThrowIfNull(background);
        ArgumentNullException.ThrowIfNull(onScanQueueFailure);

        stopMonitor();
        try
        {
            addPath();
        }
        catch
        {
            // Nothing was queued that would start the monitor again.
            startMonitor();
            throw;
        }

        // The queued scan restarts real-time monitoring; if queueing it fails, do that here.
        background(() =>
        {
            try
            {
                queueScan();
            }
            catch (Exception ex)
            {
                onScanQueueFailure(ex);
                startMonitor();
            }
        });
    }
}
