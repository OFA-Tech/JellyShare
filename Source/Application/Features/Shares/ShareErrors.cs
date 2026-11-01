using JellyShare.Domain.Results;

namespace JellyShare.Application.Features.Shares;

/// <summary>Errors of the share use cases.</summary>
public static class ShareErrors
{
    /// <summary>The share does not exist (404).</summary>
    public static readonly Error NotFound = Error.NotFound("Share.NotFound", "Share not found.");
}