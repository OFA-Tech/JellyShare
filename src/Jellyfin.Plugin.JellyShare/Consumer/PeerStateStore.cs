using System.Text.Json;
using Jellyfin.Plugin.JellyShare.Protocol;

namespace Jellyfin.Plugin.JellyShare.Consumer;

/// <summary>What the consumer remembers about one peer between syncs.</summary>
public sealed class PeerState
{
    /// <summary>Gets or sets the last catalog fetched successfully.</summary>
    public CatalogResponse? LastGoodCatalog { get; set; }

    /// <summary>Gets or sets the ETag of <see cref="LastGoodCatalog"/>.</summary>
    public string? Etag { get; set; }

    /// <summary>Gets or sets when the peer last answered (200 or 304).</summary>
    public DateTimeOffset? LastSuccessUtc { get; set; }

    /// <summary>Gets or sets when the current run of failures started; <c>null</c> while the peer is healthy.</summary>
    public DateTimeOffset? FailingSinceUtc { get; set; }

    /// <summary>Gets or sets the last error (never contains the token).</summary>
    public string? LastError { get; set; }
}

/// <summary>The peer state file exists but cannot be read.</summary>
public sealed class PeerStateUnreadableException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="PeerStateUnreadableException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="inner">The cause.</param>
    public PeerStateUnreadableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>Persists <see cref="PeerState"/> per peer id as JSON (atomic writes).</summary>
public sealed class PeerStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string _path;

    /// <summary>Initializes a new instance of the <see cref="PeerStateStore"/> class.</summary>
    /// <param name="path">The JSON file path (for example <c>&lt;data&gt;/peer-state.json</c>).</param>
    public PeerStateStore(string path)
    {
        _path = path;
    }

    /// <summary>
    /// Loads the states. Only a missing file means "no state"; a file that exists but cannot be read or parsed
    /// throws, because treating it as empty would drop every failing peer's last good catalog (and its placeholders).
    /// The file is left untouched.
    /// </summary>
    /// <returns>Peer id to state.</returns>
    /// <exception cref="PeerStateUnreadableException">The file exists but cannot be read.</exception>
    public Dictionary<Guid, PeerState> Load()
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<Guid, PeerState>();
        }

        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return JsonSerializer.Deserialize<Dictionary<Guid, PeerState>>(stream, JsonOptions)
                ?? throw new PeerStateUnreadableException("JellyShare peer state file is empty: " + _path);
        }
        catch (FileNotFoundException)
        {
            return new Dictionary<Guid, PeerState>();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new PeerStateUnreadableException("JellyShare peer state file cannot be read: " + _path, ex);
        }
    }

    /// <summary>Saves the states atomically.</summary>
    /// <param name="states">Peer id to state.</param>
    public void Save(IReadOnlyDictionary<Guid, PeerState> states)
        => AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(states, JsonOptions));
}
