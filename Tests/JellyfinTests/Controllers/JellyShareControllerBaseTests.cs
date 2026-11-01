using JellyShare.Domain.Enums;
using JellyShare.Domain.Interfaces.Messaging;
using JellyShare.Domain.Models.Protocol;
using JellyShare.Domain.Results;
using JellyShare.Jellyfin.Controllers.V1;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace JellyShare.JellyfinTests.Controllers;

public sealed class JellyShareControllerBaseTests
{
    [Theory]
    [InlineData(ErrorType.Validation, 400)]
    [InlineData(ErrorType.Failure, 400)]
    [InlineData(ErrorType.NotFound, 404)]
    [InlineData(ErrorType.Conflict, 409)]
    [InlineData(ErrorType.Unauthorized, 401)]
    [InlineData(ErrorType.Forbidden, 403)]
    [InlineData(ErrorType.Unavailable, 503)]
    [InlineData(ErrorType.Unexpected, 500)]
    public void ErrorResult_MapsTheTypeToItsStatus_WithTheErrorBody(ErrorType type, int status)
    {
        var result = new Probe().Map(new Error("X.Code", "the message", type));

        Assert.Equal(status, result.StatusCode ?? 200);
        Assert.Equal("the message", Assert.IsType<ErrorResponse>(result.Value).Error);
    }

    [Fact]
    public void ErrorResult_KeepsTheMvcResultTypesTheControllersReturnedBefore()
    {
        var probe = new Probe();

        Assert.IsType<BadRequestObjectResult>(probe.Map(Error.Validation("c", "d")));
        Assert.IsType<NotFoundObjectResult>(probe.Map(Error.NotFound("c", "d")));
        Assert.IsType<ConflictObjectResult>(probe.Map(Error.Conflict("c", "d")));
    }

    [Fact]
    public void ToActionResult_SuccessWithoutValue_IsNoContent_WithValue_IsOk()
    {
        var probe = new Probe();

        Assert.IsType<NoContentResult>(probe.Convert(Result.Success()));
        Assert.Equal(7, Assert.IsType<OkObjectResult>(probe.Convert(Result.Success(7))).Value);
    }

    private sealed class Probe : JellyShareControllerBase
    {
        public Probe()
            : base(new NoSender())
        {
        }

        public ObjectResult Map(Error error) => ErrorResult(error);

        public IActionResult Convert(Result result) => ToActionResult(result);

        public IActionResult Convert<T>(Result<T> result) => ToActionResult(result);
    }

    private sealed class NoSender : ISender
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
