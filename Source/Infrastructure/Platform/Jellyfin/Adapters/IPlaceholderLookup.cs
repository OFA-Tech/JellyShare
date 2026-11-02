namespace JellyShare.Jellyfin.Adapters;

/// <summary>What the PlaybackInfo filter needs to know about items and callers.</summary>
internal interface IPlaceholderLookup
{
    /// <summary>Finds a relay placeholder item.</summary>
    /// <param name="itemId">The library item id.</param>
    /// <returns>The placeholder, or <c>null</c> when the item is not a relay placeholder.</returns>
    PlaceholderMedia? Find(Guid itemId);

    /// <summary>Gets the bitrate limits of the caller that do not come from the request itself.</summary>
    /// <param name="caller">Who is calling (resolved from the request by the host).</param>
    /// <returns>The limits.</returns>
    Task<CallerLimits> GetCallerLimitsAsync(CallerContext caller);
}
