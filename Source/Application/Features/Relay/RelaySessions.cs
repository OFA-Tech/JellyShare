namespace JellyShare.Application.Features.Relay;

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
    private readonly Dictionary<string, (Guid Peer, DateTimeOffset Start, DateTimeOffset LastSeen, CancellationTokenSource Stop)> _sessions = new(StringComparer.Ordinal);

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
            _sessions[key] = isNew ? (peerId, now, now, new CancellationTokenSource()) : s with { LastSeen = now };
            return isNew;
        }
    }

    /// <summary>
    /// Claims a slot on a peer for a title, atomically with the limit check: two titles asked at once for a peer with
    /// one slot cannot both get it. The title counts as active on the peer from now on (start = now).
    /// </summary>
    /// <param name="key">The canonical key.</param>
    /// <param name="peerId">The peer to claim on.</param>
    /// <param name="max">The peer's stream limit.</param>
    /// <param name="created">
    /// <c>true</c> when this call made the entry, so only this caller may undo it with <see cref="CancelReservation"/>.
    /// </param>
    /// <returns><c>true</c> when the title already plays on the peer, or a slot was free and is now taken.</returns>
    public bool TryReserve(string key, Guid peerId, int max, out bool created)
    {
        var now = _clock.GetUtcNow();
        lock (_lock)
        {
            Purge(now);
            created = false;
            if (_sessions.TryGetValue(key, out var s) && s.Peer == peerId)
            {
                return true;
            }

            if (_sessions.Count(e => e.Value.Peer == peerId) >= max)
            {
                return false;
            }

            _sessions[key] = (peerId, now, now, new CancellationTokenSource());
            created = true;
            return true;
        }
    }

    /// <summary>Undoes a claim made by <see cref="TryReserve"/>, unless the title has meanwhile moved to another peer.</summary>
    /// <param name="key">The canonical key.</param>
    /// <param name="peerId">The peer the claim was made on.</param>
    public void CancelReservation(string key, Guid peerId)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(key, out var s) && s.Peer == peerId)
            {
                _sessions.Remove(key);
            }
        }
    }

    /// <summary>Lists the titles playing on a peer, oldest playback first (ties by key).</summary>
    /// <param name="peerId">The serving peer.</param>
    /// <returns>The canonical keys.</returns>
    public IReadOnlyList<string> ActiveTitles(Guid peerId)
    {
        var now = _clock.GetUtcNow();
        lock (_lock)
        {
            Purge(now);
            return _sessions
                .Where(e => e.Value.Peer == peerId)
                .OrderBy(e => e.Value.Start)
                .ThenBy(e => e.Key, StringComparer.Ordinal)
                .Select(e => e.Key)
                .ToList();
        }
    }

    /// <summary>
    /// Gets a token that is cancelled when the playback of a title is released, so a relay body still being copied
    /// for it stops pulling from the peer. A new playback of the title gets a fresh token.
    /// </summary>
    /// <param name="key">The canonical key.</param>
    /// <returns>The token, or <see cref="CancellationToken.None"/> when no playback is active.</returns>
    public CancellationToken StopToken(string key)
    {
        lock (_lock)
        {
            return _sessions.TryGetValue(key, out var s) ? s.Stop.Token : CancellationToken.None;
        }
    }

    /// <summary>Ends the playback of a title at once, freeing its peer slot and ending its relay body copies.</summary>
    /// <param name="key">The canonical key.</param>
    public void Release(string key)
    {
        CancellationTokenSource? stop;
        lock (_lock)
        {
            stop = _sessions.Remove(key, out var s) ? s.Stop : null;
        }

        // Outside the lock: cancellation runs the relay's callbacks synchronously.
        stop?.Cancel();
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
