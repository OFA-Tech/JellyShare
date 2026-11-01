using JellyShare.Domain.Results;

namespace JellyShare.Domain.Interfaces.Messaging;

/// <summary>
/// Validates a request before its handler runs (hand-written; no validation library is shipped). Only for checks that
/// need no state the handler looks up first, so the order of the error messages stays the handler's.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
public interface IValidator<in TRequest>
{
    /// <summary>Validates the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The first problem, or <c>null</c> when the request is valid.</returns>
    Error? Validate(TRequest request);
}