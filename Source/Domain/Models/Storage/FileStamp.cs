namespace JellyShare.Domain.Models.Storage;

/// <summary>When a persisted file last changed: its write time and length. <c>default</c> when the file is missing.</summary>
/// <param name="WriteUtc">The last write time (UTC).</param>
/// <param name="Length">The length in bytes.</param>
public readonly record struct FileStamp(DateTime WriteUtc, long Length);
