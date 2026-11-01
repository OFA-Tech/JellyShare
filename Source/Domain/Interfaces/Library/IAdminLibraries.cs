using JellyShare.Domain.Models.Admin;

namespace JellyShare.Domain.Interfaces.Library;

/// <summary>Library operations needed by the admin page, abstracted so the controller stays unit-testable.</summary>
public interface IAdminLibraries
{
    /// <summary>Lists the libraries.</summary>
    /// <returns>The libraries.</returns>
    IReadOnlyList<LibraryInfo> List();

    /// <summary>Adds a folder to a library and refreshes it (one scan, so the root gets its folder item).</summary>
    /// <param name="libraryName">The library name.</param>
    /// <param name="path">The folder.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task that completes when the path is added; the scan continues in the background.</returns>
    Task AddPathAsync(string libraryName, string path, CancellationToken ct);
}
