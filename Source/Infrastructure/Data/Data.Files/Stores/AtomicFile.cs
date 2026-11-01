using System.Text;

namespace JellyShare.Data.Files.Stores;

/// <summary>Writes files so readers see either the old or the new content, never a partial file.</summary>
internal static class AtomicFile
{
    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>Writes UTF-8 text through a temp file in the same directory, then moves it over the target.</summary>
    /// <param name="path">The target path.</param>
    /// <param name="content">The content.</param>
    /// <param name="beforeCommit">Test hook called with (temp path, target path) before the move.</param>
    public static void WriteAllText(string path, string content, Action<string, string>? beforeCommit = null)
        => Write(path, temp => File.WriteAllText(temp, content, Utf8), beforeCommit);

    /// <summary>Copies a stream through a temp file in the same directory, then moves it over the target.</summary>
    /// <param name="path">The target path.</param>
    /// <param name="source">The content.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task.</returns>
    public static async Task WriteStreamAsync(string path, Stream source, CancellationToken ct)
    {
        var temp = TempPathFor(path);
        try
        {
            await using (var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await source.CopyToAsync(target, ct).ConfigureAwait(false);
            }

            File.Move(temp, path, true);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static void Write(string path, Action<string> writeTemp, Action<string, string>? beforeCommit)
    {
        var temp = TempPathFor(path);
        try
        {
            writeTemp(temp);
            beforeCommit?.Invoke(temp, path);
            File.Move(temp, path, true);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static string TempPathFor(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
    }

    private static void TryDelete(string temp)
    {
        try
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
