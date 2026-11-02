using System.Text.Json;
using JellyShare.Domain.Exceptions;
using JellyShare.Domain.Interfaces.Storage;
using JellyShare.Domain.Models.Sync;

namespace JellyShare.Data.Files.Stores;

/// <summary>Persists <see cref="PeerState"/> per peer id as JSON (atomic writes).</summary>
public sealed class PeerStateStore : IPeerStateStore
{
    /// <summary>The file name in the plugin data folder.</summary>
    public const string FileName = "peer-state.json";

    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = false };

    private readonly Func<string> _pathOf;

    /// <summary>Initializes a new instance of the <see cref="PeerStateStore"/> class.</summary>
    /// <param name="path">The JSON file path (for example <c>&lt;data&gt;/peer-state.json</c>).</param>
    public PeerStateStore(string path)
        : this(() => path)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="PeerStateStore"/> class.</summary>
    /// <param name="path">Returns the JSON file path (resolved on every load and save).</param>
    public PeerStateStore(Func<string> path)
    {
        _pathOf = path;
    }

    /// <summary>Creates the store for <see cref="FileName"/> in the plugin data folder.</summary>
    /// <param name="dataDir">Returns the plugin data folder.</param>
    /// <returns>The store.</returns>
    public static PeerStateStore InDataFolder(Func<string> dataDir) => new(() => Path.Combine(dataDir(), FileName));

    /// <summary>
    /// Loads the states. Only a missing file means "no state"; a file that exists but cannot be read or parsed
    /// throws, because treating it as empty would drop every failing peer's last good catalog (and its placeholders).
    /// The file is left untouched.
    /// </summary>
    /// <returns>Peer id to state.</returns>
    /// <exception cref="PeerStateUnreadableException">The file exists but cannot be read.</exception>
    public Dictionary<Guid, PeerState> Load()
    {
        var path = _pathOf();
        if (!File.Exists(path))
        {
            return new Dictionary<Guid, PeerState>();
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return JsonSerializer.Deserialize<Dictionary<Guid, PeerState>>(stream, _jsonOptions)
                ?? throw new PeerStateUnreadableException("JellyShare peer state file is empty: " + path);
        }
        catch (FileNotFoundException)
        {
            return new Dictionary<Guid, PeerState>();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new PeerStateUnreadableException("JellyShare peer state file cannot be read: " + path, ex);
        }
    }

    /// <summary>Saves the states atomically.</summary>
    /// <param name="states">Peer id to state.</param>
    public void Save(IReadOnlyDictionary<Guid, PeerState> states)
        => AtomicFile.WriteAllText(_pathOf(), JsonSerializer.Serialize(states, _jsonOptions));
}
