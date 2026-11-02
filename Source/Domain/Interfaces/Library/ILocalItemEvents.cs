using JellyShare.Domain.Models.Library;

namespace JellyShare.Domain.Interfaces.Library;

/// <summary>Source of library item-added / item-removed notifications.</summary>
public interface ILocalItemEvents
{
    /// <summary>Starts delivering events to <paramref name="handler"/>. Handlers must be quick and must not throw.</summary>
    /// <param name="handler">The handler.</param>
    /// <returns>A token that stops the delivery when disposed.</returns>
    IDisposable Subscribe(Action<LocalItemEvent> handler);
}
