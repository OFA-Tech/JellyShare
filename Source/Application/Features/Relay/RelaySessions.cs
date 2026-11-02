namespace JellyShare.Application.Features.Relay;

/// <summary>
/// Remembers which peer serves a title while it is being played. One playback issues many relay requests
/// (the PlaybackInfo probe, Range requests, HLS seeks that reopen the URL); they all stay on the serving peer, so
/// the provider counts one stream slot, and the relay logs one line per playback. A playback ends after
/// <see cref="Grace"/> without requests and without a body copy in progress (see <see cref="Lease"/>).
/// </summary>
public sealed class RelaySessions
{
    /// <summary>How long a playback stays active after its last relay request (same as the provider's limiter).</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _clock;
    private readonly object _lock = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);

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
            return Seen(key, peerId, now, out _);
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

            _sessions[key] = new Session(peerId, now);
            created = true;
            return true;
        }
    }

    /// <summary>Undoes a claim made by <see cref="TryReserve"/>, unless the title has meanwhile moved to another peer or a body copy holds a lease on it.</summary>
    /// <param name="key">The canonical key.</param>
    /// <param name="peerId">The peer the claim was made on.</param>
    public void CancelReservation(string key, Guid peerId)
    {
        lock (_lock)
        {
            // A lease means another request of this title is copying a body: that playback must stay counted.
            if (_sessions.TryGetValue(key, out var s) && s.Peer == peerId && s.Leases == 0)
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
    /// Does what <see cref="Touch"/> does and, in the same step, leases the playback for a relay body copy: while a
    /// lease is open the playback never expires (the grace counts from when the last lease ends), and the returned
    /// token is cancelled when the playback is released, so the copy stops pulling from the peer.
    /// </summary>
    /// <param name="key">The canonical key.</param>
    /// <param name="peerId">The serving peer.</param>
    /// <returns>
    /// Whether this starts a new playback, the stop token, and the lease to dispose when the copy ends. A lease
    /// disposed after its playback was released or replaced does not affect the new one.
    /// </returns>
    public (bool IsNew, CancellationToken Stop, IDisposable Lease) Lease(string key, Guid peerId)
    {
        var now = _clock.GetUtcNow();
        lock (_lock)
        {
            Purge(now);
            var isNew = Seen(key, peerId, now, out var session);
            session.Leases++;
            return (isNew, session.Stop.Token, new LeaseHandle(this, session));
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

    private bool Seen(string key, Guid peerId, DateTimeOffset now, out Session session)
    {
        if (_sessions.TryGetValue(key, out var existing) && existing.Peer == peerId)
        {
            existing.LastSeen = now;
            session = existing;
            return false;
        }

        session = _sessions[key] = new Session(peerId, now);
        return true;
    }

    private void EndLease(Session session)
    {
        var now = _clock.GetUtcNow();
        lock (_lock)
        {
            session.Leases--;
            session.LastSeen = now;
        }
    }

    private void Purge(DateTimeOffset now)
    {
        List<string>? expired = null;
        foreach (var (key, s) in _sessions)
        {
            if (s.Leases == 0 && now - s.LastSeen >= Grace)
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

    // A replaced or released Session is no longer in the dictionary, so a late lease end only changes the old object.
    private sealed class Session
    {
        public Session(Guid peer, DateTimeOffset now)
        {
            Peer = peer;
            Start = now;
            LastSeen = now;
        }

        public Guid Peer { get; }

        public DateTimeOffset Start { get; }

        public DateTimeOffset LastSeen { get; set; }

        public int Leases { get; set; }

        public CancellationTokenSource Stop { get; } = new();
    }

    private sealed class LeaseHandle : IDisposable
    {
        private readonly RelaySessions _owner;
        private readonly Session _session;
        private int _disposed;

        public LeaseHandle(RelaySessions owner, Session session)
        {
            _owner = owner;
            _session = session;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.EndLease(_session);
            }
        }
    }
}
