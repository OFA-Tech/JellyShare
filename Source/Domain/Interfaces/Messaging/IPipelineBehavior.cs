namespace JellyShare.Domain.Interfaces.Messaging;

/// <summary>The next step of the pipeline (the next behavior, or the handler).</summary>
/// <typeparam name="TResponse">The response type.</typeparam>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>The response.</returns>
public delegate Task<TResponse> RequestHandlerDelegate<TResponse>(CancellationToken cancellationToken);

/// <summary>Wraps the handling of a request (logging, validation).</summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
public interface IPipelineBehavior<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    /// <summary>Handles the request, calling <paramref name="next"/> to continue.</summary>
    /// <param name="request">The request.</param>
    /// <param name="next">The rest of the pipeline.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The response.</returns>
    Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken);
}