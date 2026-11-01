namespace Jellyfin.Plugin.JellyShare.Provider;

/// <summary>
/// Limits the number of concurrent streams per share. A stream is a session of (share, item):
/// every request for an active session refreshes it, so Range requests, seeks and the probe that
/// precedes playback never count as extra streams. Sessions expire after <see cref="SessionTimeout"/> of idleness.
/// </summary>
public sealed class StreamLimiter
{
    /// <summary>How long a session stays active after its last request.</summary>
    public static readonly TimeSpan SessionTimeout = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _clock;
    private readonly object _lock = new();
    private readonly Dictionary<(Guid Share, Guid Item), DateTimeOffset> _lastSeen = new();

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
        isNewSession = false;
        var now = _clock.GetUtcNow();
        lock (_lock)
        {
            Purge(now);
            var key = (shareId, itemId);
            if (_lastSeen.ContainsKey(key))
            {
                _lastSeen[key] = now;
                return true;
            }

            if (maxStreams <= 0 || CountLocked(shareId) >= maxStreams)
            {
                return false;
            }

            _lastSeen[key] = now;
            isNewSession = true;
            return true;
        }
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

    private int CountLocked(Guid shareId)
    {
        var count = 0;
        foreach (var key in _lastSeen.Keys)
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
        foreach (var (key, seen) in _lastSeen)
        {
            if (now - seen >= SessionTimeout)
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
            _lastSeen.Remove(key);
        }
    }
}
