namespace JellyShare.Jellyfin.Consumer;

/// <summary>
/// Remembers which peer serves a title while it is being played. One playback issues many relay requests
/// (the PlaybackInfo probe, Range requests, HLS seeks that reopen the URL); they all stay on the serving peer, so
/// the provider counts one stream slot, and the relay logs one line per playback. A playback ends after
/// <see cref="Grace"/> without requests.
/// </summary>
public sealed class RelaySessions
{
    /// <summary>How long a playback stays active after its last relay request (same as the provider's limiter).</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _clock;
    private readonly object _lock = new();
    private readonly Dictionary<string, (Guid Peer, DateTimeOffset LastSeen)> _sessions = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="RelaySessions"/> class.</summary>
    /// <param name="clock">The clock.</param>
    public RelaySessions(TimeProvider clock)
    {
        _clock = clock;
    }

    /// <summary>Gets the peer serving an active playback of a title.</summary>
    /// <param name="key">The canonical key.</param>
    /// <returns>The peer id, or <c>null</c> when no playback is active.</returns>
    public Guid? ActivePeer(string key)
    {
        var now = _clock.GetUtcNow();
        lock (_lock)
        {
            Purge(now);
            return _sessions.TryGetValue(key, out var s) ? s.Peer : null;
        }
    }

    /// <summary>Records that a peer answered a relay request for a title.</summary>
    /// <param name="key">The canonical key.</param>
    /// <param name="peerId">The serving peer.</param>
    /// <returns><c>true</c> when this starts a new playback (none active, or another peer took over).</returns>
    public bool Touch(string key, Guid peerId)
    {
        var now = _clock.GetUtcNow();
        lock (_lock)
        {
            Purge(now);
            var isNew = !_sessions.TryGetValue(key, out var s) || s.Peer != peerId;
            _sessions[key] = (peerId, now);
            return isNew;
        }
    }

    private void Purge(DateTimeOffset now)
    {
        List<string>? expired = null;
        foreach (var (key, s) in _sessions)
        {
            if (now - s.LastSeen >= Grace)
            {
                (expired ??= new()).Add(key);
            }
        }

        if (expired is null)
        {
            return;
        }

        foreach (var key in expired)
        {
            _sessions.Remove(key);
        }
    }
}
