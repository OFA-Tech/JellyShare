using JellyShare.Domain.Enums;
using JellyShare.Domain.Results;

namespace JellyShare.UnitTests.Dispatching;

public sealed class ResultTests
{
    [Fact]
    public void Success_HasNoError()
    {
        var result = Result.Success();

        result.IsSuccess.ShouldBeTrue();
        result.IsFailure.ShouldBeFalse();
        result.Error.ShouldBeSameAs(Error.None);
    }

    [Fact]
    public void SuccessWithValue_ExposesTheValue()
    {
        var result = Result.Success(42);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(42);
    }

    [Fact]
    public void Failure_CarriesTheError_AndHidesTheValue()
    {
        var error = Error.NotFound("Thing.NotFound", "Thing not found.");

        var result = Result.Failure<int>(error);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBeSameAs(error);
        ShouldExactly.Throw<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void ImplicitConversion_FromError_IsAFailure()
    {
        Result<string> result = Error.Conflict("X.Conflict", "conflict");

        result.IsFailure.ShouldBeTrue();
        result.Error.Type.ShouldBe(ErrorType.Conflict);
    }

    [Fact]
    public void Failure_WithoutAnError_IsRejected()
        => ShouldExactly.Throw<ArgumentException>(() => Result.Failure(Error.None));

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

        error.Type.ShouldBe(type);
        error.Code.ShouldBe("c");
        error.Description.ShouldBe("d");
    }
}
