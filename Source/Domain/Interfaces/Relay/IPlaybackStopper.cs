namespace JellyShare.Domain.Interfaces.Relay;

/// <summary>Stops the host's playbacks of a title (stop-oldest when an account peer is full).</summary>
public interface IPlaybackStopper
{
    /// <summary>Shows a message to every session playing a title, then tells it to stop.</summary>
    /// <param name="canonicalKey">The canonical key of the title.</param>
    /// <param name="message">The message shown to the viewer.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The number of sessions told to stop.</returns>
    Task<int> StopTitleAsync(string canonicalKey, string message, CancellationToken ct);
}
