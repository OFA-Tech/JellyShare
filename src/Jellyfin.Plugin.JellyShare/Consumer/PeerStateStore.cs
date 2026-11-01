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

    /// <summary>Loads the states; a missing or unreadable file yields an empty map.</summary>
    /// <returns>Peer id to state.</returns>
    public Dictionary<Guid, PeerState> Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new Dictionary<Guid, PeerState>();
            }

            using var stream = File.OpenRead(_path);
            return JsonSerializer.Deserialize<Dictionary<Guid, PeerState>>(stream, JsonOptions) ?? new Dictionary<Guid, PeerState>();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new Dictionary<Guid, PeerState>();
        }
    }

    /// <summary>Saves the states atomically.</summary>
    /// <param name="states">Peer id to state.</param>
    public void Save(IReadOnlyDictionary<Guid, PeerState> states)
        => AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(states, JsonOptions));
}
