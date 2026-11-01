using System.Diagnostics.CodeAnalysis;

namespace JellyShare.Jellyfin.Provider;

/// <summary>
/// Limits the number of concurrent streams per share. A stream is a session of (share, item):
/// every request for an active session refreshes it, so Range requests, seeks and the probe that
/// precedes playback never count as extra streams. A session never expires while a response for it is
/// still being sent (an open <see cref="StreamLease"/>); after the last one ends it expires after
/// <see cref="SessionTimeout"/> of idleness.
/// </summary>
public sealed class StreamLimiter
{
    /// <summary>How long a session stays active after its last request or response ended.</summary>
    public static readonly TimeSpan SessionTimeout = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _clock;
    private readonly object _lock = new();
    private readonly Dictionary<(Guid Share, Guid Item), Session> _sessions = new();

    /// <summary>Initializes a new instance of the <see cref="StreamLimiter"/> class.</summary>
    /// <param name="clock">The clock used to expire sessions.</param>
    public StreamLimiter(TimeProvider clock)
    {
        _clock = clock;
    }

    /// <summary>Registers a request for a stream.</summary>
    /// <param name="shareId">The share making the request.</param>
    /// <param name="itemId">The item requested.</param>
    /// <param name="maxStreams">The share's concurrent stream limit.</param>
    /// <returns><c>true</c> when the request is allowed.</returns>
    public bool TryAcquire(Guid shareId, Guid itemId, int maxStreams)
        => TryAcquire(shareId, itemId, maxStreams, out _);

    /// <summary>Registers a request for a stream.</summary>
    /// <param name="shareId">The share making the request.</param>
    /// <param name="itemId">The item requested.</param>
    /// <param name="maxStreams">The share's concurrent stream limit.</param>
    /// <param name="isNewSession"><c>true</c> when the request opened a new session.</param>
    /// <returns><c>true</c> when the request is allowed.</returns>
    public bool TryAcquire(Guid shareId, Guid itemId, int maxStreams, out bool isNewSession)
    {
        var session = Acquire(shareId, itemId, maxStreams, hold: false, out isNewSession);
        return session is not null;
    }

    /// <summary>
    /// Registers a request for a stream and holds its session until the returned lease is disposed
    /// (call it when the response body has been sent).
    /// </summary>
    /// <param name="shareId">The share making the request.</param>
    /// <param name="itemId">The item requested.</param>
    /// <param name="maxStreams">The share's concurrent stream limit.</param>
    /// <param name="lease">The lease, when allowed.</param>
    /// <returns><c>true</c> when the request is allowed.</returns>
    public bool TryAcquireLease(Guid shareId, Guid itemId, int maxStreams, [NotNullWhen(true)] out StreamLease? lease)
    {
        var key = (shareId, itemId);
        var session = Acquire(shareId, itemId, maxStreams, hold: true, out var isNew);
        lease = session is null ? null : new StreamLease(this, key, session, isNew);
        return lease is not null;
    }

    /// <summary>Gets the number of active sessions of a share.</summary>
    /// <param name="shareId">The share.</param>
    /// <returns>The active session count.</returns>
    public int Active(Guid shareId)
    {
        var now = _clock.GetUtcNow();
        lock (_lock)
        {
            Purge(now);
            return CountLocked(shareId);
        }
    }

    internal void Release((Guid, Guid) key, Session session, bool removeIfUnheld)
    {
        var now = _clock.GetUtcNow();
        lock (_lock)
        {
            session.Holds--;
            session.LastSeen = now;
            if (removeIfUnheld && session.Holds == 0 && _sessions.TryGetValue(key, out var current) && ReferenceEquals(current, session))
            {
                _sessions.Remove(key);
            }
        }
    }

    private Session? Acquire(Guid shareId, Guid itemId, int maxStreams, bool hold, out bool isNewSession)
    {
        isNewSession = false;
        var now = _clock.GetUtcNow();
        lock (_lock)
        {
            Purge(now);
            var key = (shareId, itemId);
            if (!_sessions.TryGetValue(key, out var session))
            {
                if (maxStreams <= 0 || CountLocked(shareId) >= maxStreams)
                {
                    return null;
                }

                session = new Session();
                _sessions[key] = session;
                isNewSession = true;
            }

            session.LastSeen = now;
            if (hold)
            {
                session.Holds++;
            }

            return session;
        }
    }

    private int CountLocked(Guid shareId)
    {
        var count = 0;
        foreach (var key in _sessions.Keys)
        {
            if (key.Share == shareId)
            {
                count++;
            }
        }

        return count;
    }

    private void Purge(DateTimeOffset now)
    {
        List<(Guid, Guid)>? expired = null;
        foreach (var (key, session) in _sessions)
        {
            if (session.Holds == 0 && now - session.LastSeen >= SessionTimeout)
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

    internal sealed class Session
    {
        public DateTimeOffset LastSeen { get; set; }

        public int Holds { get; set; }
    }
}

/// <summary>Holds a stream session open while its response is being sent.</summary>
public sealed class StreamLease : IDisposable
{
    private readonly StreamLimiter _limiter;
    private readonly (Guid, Guid) _key;
    private readonly StreamLimiter.Session _session;
    private int _released;

    internal StreamLease(StreamLimiter limiter, (Guid, Guid) key, StreamLimiter.Session session, bool isNewSession)
    {
        _limiter = limiter;
        _key = key;
        _session = session;
        IsNewSession = isNewSession;
    }

    /// <summary>Gets a value indicating whether this request opened the session.</summary>
    public bool IsNewSession { get; }

    /// <summary>Releases the lease without serving anything: a session this request opened is dropped at once.</summary>
    public void Abort()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _limiter.Release(_key, _session, removeIfUnheld: IsNewSession);
        }
    }

    /// <summary>Releases the lease; the session then expires after <see cref="StreamLimiter.SessionTimeout"/> of idleness.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _limiter.Release(_key, _session, removeIfUnheld: false);
        }
    }
}
