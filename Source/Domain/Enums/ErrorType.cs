namespace JellyShare.Domain.Enums;

/// <summary>The kind of a use case failure; the HTTP layer maps it to a status code.</summary>
public enum ErrorType
{
    /// <summary>No error.</summary>
    None = 0,

    /// <summary>The request is invalid (400).</summary>
    Validation = 1,

    /// <summary>The operation failed for a reason the caller can fix (400).</summary>
    Failure = 2,

    /// <summary>Something the request names does not exist (404).</summary>
    NotFound = 3,

    /// <summary>The request conflicts with the current state (409).</summary>
    Conflict = 4,

    /// <summary>The caller is not authenticated (401).</summary>
    Unauthorized = 5,

    /// <summary>The caller may not do this (403).</summary>
    Forbidden = 6,

    /// <summary>A resource is temporarily unavailable (503).</summary>
    Unavailable = 7,

    /// <summary>An unexpected server-side failure (500).</summary>
    Unexpected = 8,
}
