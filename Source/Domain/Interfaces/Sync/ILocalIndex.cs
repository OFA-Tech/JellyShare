using JellyShare.Domain.Models.Library;
using JellyShare.Domain.Models.Sync;

namespace JellyShare.Domain.Interfaces.Sync;

/// <summary>Builds the set of titles the owner has locally.</summary>
public interface ILocalIndex
{
    /// <summary>Builds the index.</summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The local keys and series.</returns>
    Task<LocalIndexResult> BuildAsync(CancellationToken ct);

    /// <summary>
    /// Builds the index for the titles that have any of the given keys, reading only the titles in
    /// <paramref name="scope"/> from the library; only those files are checked.
    /// </summary>
    /// <param name="keys">The keys of interest.</param>
    /// <param name="scope">The provider ids to read from the library (covering <paramref name="keys"/>).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The local keys (restricted to titles matching <paramref name="keys"/>) and the series in scope.</returns>
    Task<LocalIndexResult> BuildForKeysAsync(IReadOnlySet<string> keys, LibraryScope scope, CancellationToken ct);

    /// <summary>Gets the folders of all libraries.</summary>
    /// <returns>The library locations.</returns>
    IReadOnlyList<string> LibraryLocations();
}
