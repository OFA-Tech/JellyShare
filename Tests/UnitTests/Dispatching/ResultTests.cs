using JellyShare.Domain.Enums;
using JellyShare.Domain.Results;
using Xunit;

namespace JellyShare.UnitTests.Dispatching;

public sealed class ResultTests
{
    [Fact]
    public void Success_HasNoError()
    {
        var result = Result.Success();

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Same(Error.None, result.Error);
    }

    [Fact]
    public void SuccessWithValue_ExposesTheValue()
    {
        var result = Result.Success(42);

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void Failure_CarriesTheError_AndHidesTheValue()
    {
        var error = Error.NotFound("Thing.NotFound", "Thing not found.");

        var result = Result.Failure<int>(error);

        Assert.True(result.IsFailure);
        Assert.Same(error, result.Error);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void ImplicitConversion_FromError_IsAFailure()
    {
        Result<string> result = Error.Conflict("X.Conflict", "conflict");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
    }

    [Fact]
    public void Failure_WithoutAnError_IsRejected()
        => Assert.Throws<ArgumentException>(() => Result.Failure(Error.None));

    [Theory]
    [InlineData(ErrorType.Validation)]
    [InlineData(ErrorType.Failure)]
    [InlineData(ErrorType.NotFound)]
    [InlineData(ErrorType.Conflict)]
    [InlineData(ErrorType.Unauthorized)]
    [InlineData(ErrorType.Forbidden)]
    [InlineData(ErrorType.Unavailable)]
    [InlineData(ErrorType.Unexpected)]
    public void ErrorFactories_SetTheType(ErrorType type)
    {
        var error = type switch
        {
            ErrorType.Validation => Error.Validation("c", "d"),
            ErrorType.Failure => Error.Failure("c", "d"),
            ErrorType.NotFound => Error.NotFound("c", "d"),
            ErrorType.Conflict => Error.Conflict("c", "d"),
            ErrorType.Unauthorized => Error.Unauthorized("c", "d"),
            ErrorType.Forbidden => Error.Forbidden("c", "d"),
            ErrorType.Unavailable => Error.Unavailable("c", "d"),
            _ => Error.Unexpected("c", "d"),
        };

        Assert.Equal(type, error.Type);
        Assert.Equal("c", error.Code);
        Assert.Equal("d", error.Description);
    }
}
