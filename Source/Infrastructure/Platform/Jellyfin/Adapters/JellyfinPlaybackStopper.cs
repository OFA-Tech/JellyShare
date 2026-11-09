using JellyShare.Domain.Interfaces.Relay;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace JellyShare.Jellyfin.Adapters;

/// <summary>
/// <see cref="IPlaybackStopper"/> through Jellyfin's session manager. Commands are sent without a controlling session
/// (Jellyfin then skips its remote-control permission check, docs/spike-results.md).
/// </summary>
internal sealed class JellyfinPlaybackStopper : IPlaybackStopper
{
    private const int MessageTimeoutMs = 10000;

    private readonly ISessionManager _sessions;
    private readonly IPlaceholderLookup _lookup;
    private readonly ILogger<JellyfinPlaybackStopper> _logger;

    /// <summary>Initializes a new instance of the <see cref="JellyfinPlaybackStopper"/> class.</summary>
    /// <param name="sessions">The session manager.</param>
    /// <param name="lookup">The placeholder lookup.</param>
    /// <param name="logger">The logger.</param>
    public JellyfinPlaybackStopper(ISessionManager sessions, IPlaceholderLookup lookup, ILogger<JellyfinPlaybackStopper> logger)
    {
        _sessions = sessions;
        _lookup = lookup;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> StopTitleAsync(string titleKey, string message, CancellationToken ct)
    {
        var playing = _sessions.Sessions
            .Where(s => s.NowPlayingItem is { } item && string.Equals(_lookup.Find(item.Id)?.StreamKey, titleKey, StringComparison.Ordinal))
            .Select(s => s.Id)
            .ToList();
        var stopped = 0;
        foreach (var sessionId in playing)
        {
            try
            {
                await _sessions.SendMessageCommand(null!, sessionId, new MessageCommand { Header = "JellyShare", Text = message, TimeoutMs = MessageTimeoutMs }, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "JellyShare: could not show the stop message to session {Session}", sessionId);
            }

            try
            {
                await _sessions.SendPlaystateCommand(null!, sessionId, new PlaystateRequest { Command = PlaystateCommand.Stop }, ct).ConfigureAwait(false);
                stopped++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "JellyShare: could not stop {Key} in session {Session}", titleKey, sessionId);
            }
        }

        return stopped;
    }
}
