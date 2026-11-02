namespace JellyShare.Domain.Interfaces.Messaging;

/// <summary>Dispatches a request to its handler through the pipeline behaviors.</summary>
public interface ISender
{
    /// <summary>Sends a request.</summary>
    /// <typeparam name="TResponse">The response type.</typeparam>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The response.</returns>
    Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);
}
