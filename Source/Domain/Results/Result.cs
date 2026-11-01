using System.Diagnostics.CodeAnalysis;

namespace JellyShare.Domain.Results;

/// <summary>The outcome of a use case: success, or an <see cref="Results.Error"/>.</summary>
public class Result
{
    /// <summary>Initializes a new instance of the <see cref="Result"/> class.</summary>
    /// <param name="isSuccess">Whether it succeeded.</param>
    /// <param name="error">The error; <see cref="Error.None"/> exactly when successful.</param>
    protected Result(bool isSuccess, Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        switch (isSuccess)
        {
            case true when error != Error.None:
                throw new ArgumentException("A successful result cannot carry an error.", nameof(error));
            case false when error == Error.None:
                throw new ArgumentException("A failed result must carry an error.", nameof(error));
            default:
                break;
        }

        IsSuccess = isSuccess;
        Error = error;
    }

    /// <summary>Gets a value indicating whether the use case succeeded.</summary>
    public bool IsSuccess { get; }

    /// <summary>Gets a value indicating whether the use case failed.</summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>Gets the error (<see cref="Error.None"/> on success).</summary>
    public Error Error { get; }

    /// <summary>Creates a success.</summary>
    /// <returns>The result.</returns>
    public static Result Success() => new(true, Error.None);

    /// <summary>Creates a success with a value.</summary>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="value">The value.</param>
    /// <returns>The result.</returns>
    public static Result<TValue> Success<TValue>(TValue value) => new(value, true, Error.None);

    /// <summary>Creates a failure.</summary>
    /// <param name="error">The error.</param>
    /// <returns>The result.</returns>
    public static Result Failure(Error error) => new(false, error);

    /// <summary>Creates a failure of a value-returning use case.</summary>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="error">The error.</param>
    /// <returns>The result.</returns>
    public static Result<TValue> Failure<TValue>(Error error) => new(default, false, error);
}

/// <summary>The outcome of a use case that returns a value.</summary>
/// <typeparam name="TValue">The value type.</typeparam>
public class Result<TValue> : Result
{
    /// <summary>Initializes a new instance of the <see cref="Result{TValue}"/> class.</summary>
    /// <param name="value">The value (ignored on failure).</param>
    /// <param name="isSuccess">Whether it succeeded.</param>
    /// <param name="error">The error.</param>
    protected internal Result(TValue? value, bool isSuccess, Error error)
        : base(isSuccess, error) => RawValue = value;

    /// <summary>Gets the value.</summary>
    /// <exception cref="InvalidOperationException">The result is a failure.</exception>
    [NotNull]
    public TValue Value => IsSuccess
        ? RawValue!
        : throw new InvalidOperationException("The value of a failed result cannot be accessed.");

    private TValue? RawValue { get; }

    /// <summary>Converts an error into a failed result.</summary>
    /// <param name="error">The error.</param>
    public static implicit operator Result<TValue>(Error error) => Failure<TValue>(error);
}