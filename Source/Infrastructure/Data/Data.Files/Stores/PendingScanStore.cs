using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using JellyShare.Domain.Interfaces.Storage;
using Microsoft.Extensions.Logging;

namespace JellyShare.Data.Files.Stores;

/// <summary>Persists the folders still to be reported for scanning as <c>pending-scans.json</c> in the plugin data folder.</summary>
public sealed class PendingScanStore : IPendingScanStore
{
    /// <summary>The file name in the plugin data folder.</summary>
    public const string FileName = "pending-scans.json";

    private const string TypeOnlyLogging = "Only the exception type is logged, never its message (paths, peer URLs, tokens) or stack.";

    private readonly Func<string> _dataDir;
    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="PendingScanStore"/> class.</summary>
    /// <param name="dataDir">Returns the plugin data folder.</param>
    /// <param name="logger">The logger.</param>
    public PendingScanStore(Func<string> dataDir, ILogger<PendingScanStore> logger)
    {
        _dataDir = dataDir;
        _logger = logger;
    }

    private string FilePath => Path.Combine(_dataDir(), FileName);

    /// <inheritdoc />
    public bool Exists() => File.Exists(FilePath);

    /// <inheritdoc />
    public List<string> Read()
    {
        var path = FilePath;
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? [];
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            LogListUnreadable(ex);
        }

        return [];
    }

    [SuppressMessage("Major Code Smell", "S6667:Logging in a catch clause should pass the caught exception as a parameter.", Justification = TypeOnlyLogging)]
    private void LogListUnreadable(Exception ex)
        => _logger.LogWarning("JellyShare sync: pending scan list unreadable ({Error}), starting a new one", ex.GetType().Name);

    /// <inheritdoc />
    public void Write(IReadOnlyList<string> dirs) => AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(dirs));
}
