using JellyShare.Application.Features.Admin;
using JellyShare.Domain.Interfaces.Library;
using JellyShare.Domain.Models.Admin;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging;

namespace JellyShare.Jellyfin.Adapters;

/// <summary><see cref="IAdminLibraries"/> backed by the Jellyfin library manager (the same calls the dashboard uses).</summary>
public sealed class JellyfinAdminLibraries : IAdminLibraries
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryMonitor _monitor;
    private readonly ILogger<JellyfinAdminLibraries> _logger;

    /// <summary>Initializes a new instance of the <see cref="JellyfinAdminLibraries"/> class.</summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="monitor">The library monitor.</param>
    /// <param name="logger">The logger.</param>
    public JellyfinAdminLibraries(ILibraryManager libraryManager, ILibraryMonitor monitor, ILogger<JellyfinAdminLibraries> logger)
    {
        _libraryManager = libraryManager;
        _monitor = monitor;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<LibraryInfo> List()
    {
        var result = new List<LibraryInfo>();
        foreach (var folder in _libraryManager.GetVirtualFolders())
        {
            if (!Guid.TryParse(folder.ItemId, out var id))
            {
                continue;
            }

            result.Add(new LibraryInfo(
                id,
                folder.Name,
                folder.CollectionType?.ToString().ToLowerInvariant(),
                (folder.Locations ?? []).Where(l => !string.IsNullOrWhiteSpace(l)).ToList(),
                folder.LibraryOptions?.EnableAutomaticSeriesGrouping ?? false));
        }

        return result;
    }

    /// <inheritdoc />
    public Task AddPathAsync(string libraryName, string path, CancellationToken ct)
    {
        // One library scan after adding: it creates the Folder item for the new root, which targeted placeholder scans
        // resolve to. Not awaited; it runs in the library's own scan queue.
        LibraryPaths.AddWithMonitorRecovery(
            _monitor.Stop,
            _monitor.Start,
            () => _libraryManager.AddMediaPath(libraryName, new MediaPathInfo(path)),
            _libraryManager.QueueLibraryScan,
            work => _ = Task.Run(work),
            ex => _logger.LogWarning(ex, "JellyShare admin: could not queue the library scan after adding a placeholder folder"));
        return Task.CompletedTask;
    }
}
