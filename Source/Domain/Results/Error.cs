using JellyShare.Domain.Enums;

namespace JellyShare.Domain.Results;

/// <summary>A use case failure: a stable code, a message shown to the user as-is, and its kind.</summary>
/// <param name="Code">A stable code, e.g. <c>Share.NotFound</c>.</param>
/// <param name="Description">The message (the admin page shows it verbatim; never contains a secret).</param>
/// <param name="Type">The kind of failure.</param>
public sealed record Error(string Code, string Description, ErrorType Type)
{
    /// <summary>No error (the error of a successful result).</summary>
    public static readonly Error None = new(string.Empty, string.Empty, ErrorType.None);

    /// <summary>A required value was null.</summary>
    public static readonly Error NullValue = new("General.NullValue", "A required value was null.", ErrorType.Validation);

    /// <summary>Creates a validation error.</summary>
    /// <param name="code">The code.</param>
    /// <param name="description">The message.</param>
    /// <returns>The error.</returns>
    public static Error Validation(string code, string description) => new(code, description, ErrorType.Validation);

    /// <summary>Creates a failure.</summary>
    /// <param name="code">The code.</param>
    /// <param name="description">The message.</param>
    /// <returns>The error.</returns>
    public static Error Failure(string code, string description) => new(code, description, ErrorType.Failure);

    /// <summary>Creates a not-found error.</summary>
    /// <param name="code">The code.</param>
    /// <param name="description">The message.</param>
    /// <returns>The error.</returns>
    public static Error NotFound(string code, string description) => new(code, description, ErrorType.NotFound);

    /// <summary>Creates a conflict error.</summary>
    /// <param name="code">The code.</param>
    /// <param name="description">The message.</param>
    /// <returns>The error.</returns>
    public static Error Conflict(string code, string description) => new(code, description, ErrorType.Conflict);

    /// <summary>Creates an unauthorized error.</summary>
    /// <param name="code">The code.</param>
    /// <param name="description">The message.</param>
    /// <returns>The error.</returns>
    public static Error Unauthorized(string code, string description) => new(code, description, ErrorType.Unauthorized);

    /// <summary>Creates a forbidden error.</summary>
    /// <param name="code">The code.</param>
    /// <param name="description">The message.</param>
    /// <returns>The error.</returns>
    public static Error Forbidden(string code, string description) => new(code, description, ErrorType.Forbidden);

    /// <summary>Creates an unavailable error.</summary>
    /// <param name="code">The code.</param>
    /// <param name="description">The message.</param>
    /// <returns>The error.</returns>
    public static Error Unavailable(string code, string description) => new(code, description, ErrorType.Unavailable);

    /// <summary>Creates an unexpected (server-side) error.</summary>
    /// <param name="code">The code.</param>
    /// <param name="description">The message.</param>
    /// <returns>The error.</returns>
    public static Error Unexpected(string code, string description) => new(code, description, ErrorType.Unexpected);
}