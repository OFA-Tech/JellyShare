using System.Diagnostics;
using JellyShare.Domain.Interfaces.Messaging;
using JellyShare.Domain.Results;
using Microsoft.Extensions.Logging;

namespace JellyShare.Application.Behaviors;

/// <summary>
/// Logs each request by type name only (never its content, which may hold a token) at debug level and failures with their
/// code. Exceptions pass through unchanged and unlogged: the host logs them once.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
public sealed class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private static readonly string _requestName = typeof(TRequest).Name;

    private readonly ILogger<LoggingBehavior<TRequest, TResponse>> _logger;

    /// <summary>Initializes a new instance of the <see cref="LoggingBehavior{TRequest, TResponse}"/> class.</summary>
    /// <param name="logger">The logger.</param>
    public LoggingBehavior(ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var response = await next(cancellationToken).ConfigureAwait(false);
            var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (response is Result { IsFailure: true } failure)
            {
                _logger.LogDebug("JellyShare: {RequestName} failed in {ElapsedMs}ms with {ErrorCode}", _requestName, elapsedMs, failure.Error.Code);
            }
            else
            {
                _logger.LogDebug("JellyShare: {RequestName} completed in {ElapsedMs}ms", _requestName, elapsedMs);
            }

            return response;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("JellyShare: {RequestName} was cancelled after {ElapsedMs}ms", _requestName, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
    }
}
