namespace JellyShare.Domain.Models.Library;

/// <summary>A local library item was added or removed.</summary>
/// <param name="Removed"><c>true</c> for a removal, <c>false</c> for an addition.</param>
/// <param name="Media">The item, as far as its keys are concerned, at the time of the event.</param>
/// <param name="Refresh">
/// Re-reads the item later (returns <c>null</c> if it is gone). Used for added items whose ids or season/episode
/// numbers are not set yet when the event fires.
/// </param>
public sealed record LocalItemEvent(bool Removed, LocalMedia Media, Func<LocalMedia?>? Refresh = null);
