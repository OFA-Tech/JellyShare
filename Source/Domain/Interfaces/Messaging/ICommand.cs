using System.Diagnostics.CodeAnalysis;
using JellyShare.Domain.Results;

namespace JellyShare.Domain.Interfaces.Messaging;

/// <summary>A request dispatched through <see cref="ISender"/>.</summary>
/// <typeparam name="TResponse">
/// The response type. It is never a member: it only lets <see cref="ISender.Send{TResponse}"/> infer the response type
/// from the request type.
/// </typeparam>
[SuppressMessage("Design", "CA1040:Avoid empty interfaces", Justification = "Marker interface carrying the response type for the dispatcher (template messaging contract).")]
[SuppressMessage("Major Code Smell", "S2326:Unused type parameters should be removed", Justification = "Phantom type parameter: it is what lets ISender.Send infer the response type.")]
public interface IRequest<out TResponse>;

/// <summary>A command (changes state) without a value.</summary>
[SuppressMessage("Design", "CA1040:Avoid empty interfaces", Justification = "Marker interface (template messaging contract).")]
public interface ICommand : IRequest<Result>;

/// <summary>A command (changes state) that returns a value.</summary>
/// <typeparam name="TResponse">The value type.</typeparam>
[SuppressMessage("Design", "CA1040:Avoid empty interfaces", Justification = "Marker interface (template messaging contract).")]
public interface ICommand<TResponse> : IRequest<Result<TResponse>>;

/// <summary>A query (reads state) that returns a value.</summary>
/// <typeparam name="TResponse">The value type.</typeparam>
[SuppressMessage("Design", "CA1040:Avoid empty interfaces", Justification = "Marker interface (template messaging contract).")]
public interface IQuery<TResponse> : IRequest<Result<TResponse>>;
