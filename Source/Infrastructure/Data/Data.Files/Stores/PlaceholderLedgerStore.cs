using System.Text.Json;
using JellyShare.Domain.Interfaces.Storage;
using JellyShare.Domain.Models.Placeholders;
using JellyShare.Domain.Models.Storage;

namespace JellyShare.Data.Files.Stores;

/// <summary>Persists the <see cref="PlaceholderLedger"/> as <c>placeholder-ledger.json</c> in the plugin data folder.</summary>
public sealed class PlaceholderLedgerStore : IPlaceholderLedgerStore
{
    /// <summary>The file name in the plugin data folder.</summary>
    public const string FileName = "placeholder-ledger.json";

    private readonly Func<string> _dataDir;

    /// <summary>Initializes a new instance of the <see cref="PlaceholderLedgerStore"/> class.</summary>
    /// <param name="dataDir">Returns the plugin data folder.</param>
    public PlaceholderLedgerStore(Func<string> dataDir)
    {
        _dataDir = dataDir;
    }

    private string FilePath => Path.Combine(_dataDir(), FileName);

    /// <inheritdoc />
    public PlaceholderLedger Load()
    {
        var path = FilePath;
        try
        {
            if (File.Exists(path))
            {
                var roots = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(path));
                if (roots is not null)
                {
                    return new PlaceholderLedger(roots);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Re-seeded from disk by the caller: the files there are treated as plugin-written.
        }

        return new PlaceholderLedger();
    }

    /// <inheritdoc />
    public void Save(PlaceholderLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(ledger.Roots));
    }

    /// <inheritdoc />
    public FileStamp Stamp() => FileStamps.Of(FilePath);
}
