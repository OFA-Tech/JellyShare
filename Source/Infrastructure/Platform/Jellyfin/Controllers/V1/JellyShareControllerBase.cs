using JellyShare.Domain.Enums;
using JellyShare.Domain.Interfaces.Messaging;
using JellyShare.Domain.Models.Protocol;
using JellyShare.Domain.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace JellyShare.Jellyfin.Controllers.V1;

/// <summary>
/// Base of the JellyShare controllers (template <c>ApiControllerBase</c>): dispatches use cases through
/// <see cref="ISender"/> and maps an <see cref="Error"/> to its status code with an <see cref="ErrorResponse"/> body
/// (<c>{"error": "..."}</c>), the shape peers and the admin page read.
/// </summary>
public abstract class JellyShareControllerBase : ControllerBase
{
    /// <summary>Initializes a new instance of the <see cref="JellyShareControllerBase"/> class.</summary>
    /// <param name="sender">Dispatches the use cases.</param>
    protected JellyShareControllerBase(ISender sender)
    {
        Sender = sender;
    }

    /// <summary>Gets the use case dispatcher.</summary>
    protected ISender Sender { get; }

    /// <summary>Maps an error type to its HTTP status code.</summary>
    /// <param name="type">The error type.</param>
    /// <returns>The status code.</returns>
    internal static int StatusCodeFor(ErrorType type) => type switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.Failure => StatusCodes.Status400BadRequest,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        ErrorType.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError,
    };

    /// <summary>204 on success, otherwise the error.</summary>
    /// <param name="result">The result.</param>
    /// <returns>The action result.</returns>
    protected IActionResult ToActionResult(Result result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.IsSuccess ? NoContent() : ErrorResult(result.Error);
    }

    /// <summary>200 with the value on success, otherwise the error.</summary>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="result">The result.</param>
    /// <returns>The action result.</returns>
    protected IActionResult ToActionResult<TValue>(Result<TValue> result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    /// <summary>The status code of the error with <c>{"error": description}</c>.</summary>
    /// <param name="error">The error.</param>
    /// <returns>The action result.</returns>
    protected ObjectResult ErrorResult(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var body = new ErrorResponse(error.Description);
        return error.Type switch
        {
            ErrorType.Validation or ErrorType.Failure => BadRequest(body),
            ErrorType.NotFound => NotFound(body),
            ErrorType.Conflict => Conflict(body),
            _ => StatusCode(StatusCodeFor(error.Type), body),
        };
    }

    /// <summary>400 with <c>{"error": message}</c> for a request the use case never sees (for example a missing body).</summary>
    /// <param name="message">The message.</param>
    /// <returns>The action result.</returns>
    protected BadRequestObjectResult BadRequestError(string message) => BadRequest(new ErrorResponse(message));
}
