using System.Collections.Concurrent;
using JellyShare.Domain.Interfaces.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace JellyShare.Application.Dispatching;

/// <summary>Resolves the handler of a request and runs it inside the registered pipeline behaviors (template sender).</summary>
public sealed class Sender : ISender
{
    private static readonly ConcurrentDictionary<Type, object> _wrappers = new();

    private readonly IServiceProvider _serviceProvider;

    /// <summary>Initializes a new instance of the <see cref="Sender"/> class.</summary>
    /// <param name="serviceProvider">Resolves handlers and behaviors.</param>
    public Sender(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <inheritdoc />
    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var wrapper = (IRequestHandlerWrapper<TResponse>)_wrappers.GetOrAdd(
            request.GetType(),
            static (type, responseType) =>
            {
                var wrapperType = typeof(RequestHandlerWrapper<,>).MakeGenericType(type, responseType);
                return Activator.CreateInstance(wrapperType)
                    ?? throw new InvalidOperationException($"Could not construct a dispatch wrapper for request type '{type}'.");
            },
            typeof(TResponse));

        return wrapper.Handle(request, _serviceProvider, cancellationToken);
    }

    private interface IRequestHandlerWrapper<TResponse>
    {
        Task<TResponse> Handle(IRequest<TResponse> request, IServiceProvider serviceProvider, CancellationToken cancellationToken);
    }

    private sealed class RequestHandlerWrapper<TRequest, TResponse> : IRequestHandlerWrapper<TResponse>
        where TRequest : IRequest<TResponse>
    {
        public Task<TResponse> Handle(IRequest<TResponse> request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
        {
            var handler = serviceProvider.GetService<IRequestHandler<TRequest, TResponse>>()
                ?? throw new InvalidOperationException(
                    $"No handler is registered for request type '{typeof(TRequest)}'. Handlers are discovered by "
                    + "AddDispatcherSetup: check that the handler is public, non-abstract and lives in the Application assembly.");

            var typedRequest = (TRequest)request;
            RequestHandlerDelegate<TResponse> pipeline = token => handler.Handle(typedRequest, token);

            // The first registered behavior is the outermost.
            foreach (var behavior in serviceProvider.GetServices<IPipelineBehavior<TRequest, TResponse>>().Reverse())
            {
                var next = pipeline;
                var current = behavior;
                pipeline = token => current.Handle(typedRequest, next, token);
            }

            return pipeline(cancellationToken);
        }
    }
}
