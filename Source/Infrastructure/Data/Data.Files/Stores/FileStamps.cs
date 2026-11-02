using JellyShare.Domain.Models.Storage;

namespace JellyShare.Data.Files.Stores;

/// <summary>Reads <see cref="FileStamp"/>s.</summary>
internal static class FileStamps
{
    /// <summary>Gets the write time and length of a file, or <c>default</c> when it is missing or unreadable.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The stamp.</returns>
    public static FileStamp Of(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new FileStamp(info.LastWriteTimeUtc, info.Length) : default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }
}
