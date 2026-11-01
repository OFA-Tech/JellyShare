using JellyShare.Domain.Models.Placeholders;
using JellyShare.Domain.Models.Storage;

namespace JellyShare.Domain.Interfaces.Storage;

/// <summary>Persists the <see cref="PlaceholderLedger"/> (<c>placeholder-ledger.json</c>).</summary>
public interface IPlaceholderLedgerStore
{
    /// <summary>Loads the ledger; a missing or unreadable file gives an empty ledger (roots are then seeded from disk).</summary>
    /// <returns>The ledger.</returns>
    PlaceholderLedger Load();

    /// <summary>Saves the ledger atomically.</summary>
    /// <param name="ledger">The ledger.</param>
    void Save(PlaceholderLedger ledger);

    /// <summary>Gets when the file last changed (<c>default</c> when missing).</summary>
    /// <returns>The stamp.</returns>
    FileStamp Stamp();
}
