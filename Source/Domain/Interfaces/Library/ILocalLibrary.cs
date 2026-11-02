using JellyShare.Domain.Models.Library;

namespace JellyShare.Domain.Interfaces.Library;

/// <summary>Reads the local library.</summary>
public interface ILocalLibrary
{
    /// <summary>Reads all movies, episodes and series.</summary>
    /// <returns>The snapshot.</returns>
    LocalLibrarySnapshot Read();

    /// <summary>Reads only the titles in <paramref name="scope"/> (provider-id queries, no full library read).</summary>
    /// <param name="scope">The provider ids of interest.</param>
    /// <returns>The matching movies, series and those series' episodes (no locations).</returns>
    LocalLibrarySnapshot Read(LibraryScope scope);

    /// <summary>Gets the folders of all libraries.</summary>
    /// <returns>The library locations.</returns>
    IReadOnlyList<string> GetLocations();
}
