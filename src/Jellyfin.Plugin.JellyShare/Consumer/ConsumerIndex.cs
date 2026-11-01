using Jellyfin.Plugin.JellyShare.Keys;

namespace Jellyfin.Plugin.JellyShare.Consumer;

/// <summary>Read access to the consumer's persisted merged index and peer states (written by <see cref="SyncService"/>).</summary>
public interface IConsumerIndex
{
    /// <summary>Finds a merged title by its canonical key (or any other key of the entry).</summary>
    /// <param name="canonicalKey">The key from the placeholder URL.</param>
    /// <returns>The entry, or <c>null</c> when unknown.</returns>
    MergedEntry? Find(string canonicalKey);

    /// <summary>Gets the peer states by peer id. Empty when the state file is missing or unreadable.</summary>
    /// <returns>The states.</returns>
    IReadOnlyDictionary<Guid, PeerState> PeerStates();
}

/// <summary>
/// <see cref="IConsumerIndex"/> over <c>merged-index.json</c> and <c>peer-state.json</c> in the plugin data folder.
/// Both files are re-read only when their timestamp or size changes.
/// </summary>
public sealed class ConsumerIndexFiles : IConsumerIndex
{
    /// <summary>The peer state file name (same as <see cref="SyncService"/>).</summary>
    public const string PeerStateFileName = "peer-state.json";

    private static readonly IReadOnlyDictionary<Guid, PeerState> NoStates = new Dictionary<Guid, PeerState>();

    private readonly Func<string> _dataDir;
    private readonly object _lock = new();
    private (DateTime Stamp, long Length) _indexVersion;
    private Dictionary<string, MergedEntry> _byKey = new(StringComparer.Ordinal);
    private (DateTime Stamp, long Length) _stateVersion;
    private IReadOnlyDictionary<Guid, PeerState> _states = NoStates;

    /// <summary>Initializes a new instance of the <see cref="ConsumerIndexFiles"/> class.</summary>
    /// <param name="dataDir">Returns the plugin data folder.</param>
    public ConsumerIndexFiles(Func<string> dataDir)
    {
        _dataDir = dataDir;
    }

    /// <inheritdoc />
    public MergedEntry? Find(string canonicalKey)
    {
        if (string.IsNullOrEmpty(canonicalKey))
        {
            return null;
        }

        var path = Path.Combine(_dataDir(), MergedIndexStore.FileName);
        lock (_lock)
        {
            var version = Version(path);
            // A failed read (file locked, half-visible) keeps the last good index and is retried on the next call.
            if (version != _indexVersion && MergedIndexStore.TryLoad(path, out var file))
            {
                _byKey = Build(file);
                _indexVersion = version;
            }

            return _byKey.GetValueOrDefault(canonicalKey);
        }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<Guid, PeerState> PeerStates()
    {
        var path = Path.Combine(_dataDir(), PeerStateFileName);
        lock (_lock)
        {
            var version = Version(path);
            if (version != _stateVersion)
            {
                try
                {
                    _states = new PeerStateStore(path).Load();
                    _stateVersion = version;
                }
                catch (PeerStateUnreadableException)
                {
                    // Keep the last states read and retry on the next call (the failure may be transient).
                }
            }

            return _states;
        }
    }

    private static Dictionary<string, MergedEntry> Build(MergedIndexFile? file)
    {
        var byKey = new Dictionary<string, MergedEntry>(StringComparer.Ordinal);
        if (file is null)
        {
            return byKey;
        }

        var entries = file.ToEntries();
        foreach (var entry in entries)
        {
            byKey[entry.CanonicalKey] = entry;
        }

        foreach (var entry in entries)
        {
            foreach (var key in entry.Keys)
            {
                byKey.TryAdd(key, entry);
            }
        }

        return byKey;
    }

    private static (DateTime Stamp, long Length) Version(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.LastWriteTimeUtc, info.Length) : default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }
}
