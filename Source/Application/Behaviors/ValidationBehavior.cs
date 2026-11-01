using JellyShare.Application.Dispatching;
using JellyShare.Domain.Interfaces.Messaging;
using JellyShare.Domain.Results;

namespace JellyShare.Application.Behaviors;

/// <summary>Runs the request's validators before the handler; the first problem becomes the failed result.</summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
public sealed class ValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
    where TResponse : Result
{
    private readonly IValidator<TRequest>[] _validators;

    /// <summary>Initializes a new instance of the <see cref="ValidationBehavior{TRequest, TResponse}"/> class.</summary>
    /// <param name="validators">The validators of the request type, in registration order.</param>
    public ValidationBehavior(IEnumerable<IValidator<TRequest>> validators)
    {
        _validators = validators.ToArray();
    }

    /// <inheritdoc />
    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);
        foreach (var validator in _validators)
        {
            if (validator.Validate(request) is { } error)
            {
                return Task.FromResult(ResultFactory.Failure<TResponse>(error));
            }
        }

        return next(cancellationToken);
    }
}