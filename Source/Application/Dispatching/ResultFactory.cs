using System.Collections.Concurrent;
using System.Reflection;
using JellyShare.Domain.Results;

namespace JellyShare.Application.Dispatching;

/// <summary>Creates a failed <c>Result</c> or <c>Result&lt;T&gt;</c> when only the response type is known (behaviors).</summary>
internal static class ResultFactory
{
    private static readonly ConcurrentDictionary<Type, MethodInfo> _failureMethods = new();

    private static readonly MethodInfo _genericFailureMethod = typeof(Result)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(method => method is { Name: nameof(Result.Failure), IsGenericMethodDefinition: true }
            && method.GetParameters().Length == 1);

    /// <summary>Creates a failure of the given response type.</summary>
    /// <typeparam name="TResponse"><see cref="Result"/> or <see cref="Result{TValue}"/>.</typeparam>
    /// <param name="error">The error.</param>
    /// <returns>The failed result.</returns>
    public static TResponse Failure<TResponse>(Error error)
        where TResponse : Result
    {
        if (typeof(TResponse) == typeof(Result))
        {
            return (TResponse)Result.Failure(error);
        }

        var method = _failureMethods.GetOrAdd(
            typeof(TResponse),
            static responseType => _genericFailureMethod.MakeGenericMethod(responseType.GetGenericArguments()[0]));
        return (TResponse)method.Invoke(null, [error])!;
    }
}
