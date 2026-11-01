using System.Text.Json;

namespace Jellyfin.Plugin.JellyShare.Consumer;

/// <summary>Persists the last <see cref="SyncReport"/> as JSON in the plugin data folder so the admin page survives restarts.</summary>
public static class SyncReportStore
{
    /// <summary>The file name inside the data folder.</summary>
    public const string FileName = "last-sync-report.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>Saves the report atomically.</summary>
    /// <param name="dataDir">The plugin data folder.</param>
    /// <param name="report">The report.</param>
    public static void Save(string dataDir, SyncReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        AtomicFile.WriteAllText(Path.Combine(dataDir, FileName), JsonSerializer.Serialize(report, JsonOptions));
    }

    /// <summary>Loads the persisted report.</summary>
    /// <param name="dataDir">The plugin data folder.</param>
    /// <returns>The report, or <c>null</c> when missing or unreadable.</returns>
    public static SyncReport? Load(string dataDir)
    {
        try
        {
            var path = Path.Combine(dataDir, FileName);
            return File.Exists(path) ? JsonSerializer.Deserialize<SyncReport>(File.ReadAllText(path), JsonOptions) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }
}
