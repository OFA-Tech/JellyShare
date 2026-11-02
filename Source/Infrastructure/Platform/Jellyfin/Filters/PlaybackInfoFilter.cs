using JellyShare.Application.Features.Relay;
using JellyShare.Domain.Enums;
using JellyShare.Domain.Interfaces.Configuration;
using JellyShare.Domain.Interfaces.Relay;
using JellyShare.Domain.Interfaces.Sync;
using JellyShare.Jellyfin.Adapters;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JellyShare.Jellyfin.Filters;

/// <summary>
/// Keeps clients away from relay URLs. Jellyfin hands a <c>.strm</c> URL to clients as <c>MediaSources[].Path</c>
/// with direct play allowed, and clients such as jellyfin-web then open it themselves (docs/spike-results.md,
/// Step 4). This global MVC filter:
/// <list type="bullet">
/// <item>before <c>POST /Items/{id}/PlaybackInfo</c> runs, disables direct play and direct stream for a placeholder
/// when the caller's effective max bitrate is below the source bitrate, so Jellyfin builds a transcode (Jellyfin
/// never applies bitrate limits to remote sources itself);</item>
/// <item>on every object result (PlaybackInfo, item DTOs with <c>MediaSources</c>, query results), blanks the
/// <c>Path</c> of relay sources and clears <c>SupportsDirectPlay</c>, so clients use Jellyfin's stream or transcode
/// URLs.</item>
/// </list>
/// </summary>
internal sealed class PlaybackInfoFilter : IAsyncActionFilter, IAsyncResultFilter
{
    /// <summary>The route template of Jellyfin's GET and POST PlaybackInfo actions.</summary>
    public const string PlaybackInfoTemplate = "Items/{itemId}/PlaybackInfo";

    private const string EnableDirectPlay = "enableDirectPlay";
    private const string EnableDirectStream = "enableDirectStream";

    private readonly IPlaceholderLookup _lookup;
    private readonly JellyfinCallerResolver _callers;
    private readonly IRelayBase _relayBase;
    private readonly IConsumerIndex _index;
    private readonly ISettingsProvider _config;
    private readonly RelaySessions _sessions;
    private readonly IPlaybackStopper _stopper;
    private readonly ILogger<PlaybackInfoFilter> _logger;

    /// <summary>Initializes a new instance of the <see cref="PlaybackInfoFilter"/> class.</summary>
    /// <param name="lookup">The placeholder lookup.</param>
    /// <param name="callers">Resolves the caller of a request.</param>
    /// <param name="relayBase">The relay base URL.</param>
    /// <param name="index">The merged index and peer states.</param>
    /// <param name="config">The plugin configuration (peers).</param>
    /// <param name="sessions">The active playbacks.</param>
    /// <param name="stopper">Stops the playbacks of a title.</param>
    /// <param name="logger">The logger.</param>
    public PlaybackInfoFilter(
        IPlaceholderLookup lookup,
        JellyfinCallerResolver callers,
        IRelayBase relayBase,
        IConsumerIndex index,
        ISettingsProvider config,
        RelaySessions sessions,
        IPlaybackStopper stopper,
        ILogger<PlaybackInfoFilter> logger)
    {
        _lookup = lookup;
        _callers = callers;
        _relayBase = relayBase;
        _index = index;
        _config = config;
        _sessions = sessions;
        _stopper = stopper;
        _logger = logger;
    }

    /// <summary>Registers the filter as a global MVC filter (applies to Jellyfin's own controllers).</summary>
    /// <param name="services">The service collection.</param>
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<JellyfinCallerResolver>();
        services.AddSingleton<PlaybackInfoFilter>();
        services.Configure<MvcOptions>(o => o.Filters.AddService<PlaybackInfoFilter>());
        services.AddHostedService<PlaybackInfoTargetCheck>();
    }

    /// <summary>
    /// Checks that Jellyfin still has the action the bitrate rule hooks into: a <see cref="PlaybackInfoTemplate"/> route
    /// declaring <c>itemId</c> and <c>enableDirectStream</c>. A future Jellyfin that renames them would silently turn the
    /// rule off, so this is logged as a warning at startup.
    /// </summary>
    /// <param name="actions">The registered MVC actions.</param>
    /// <returns>A warning, or <c>null</c> when the target is found.</returns>
    public static string? TargetWarning(IEnumerable<Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        var routes = actions
            .Where(a => string.Equals(a.AttributeRouteInfo?.Template, PlaybackInfoTemplate, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (routes.Count == 0)
        {
            return "JellyShare: Jellyfin's PlaybackInfo action (" + PlaybackInfoTemplate + ") was not found. Placeholders may play "
                + "above a client's bitrate limit (direct stream is not turned off). Check for a JellyShare update.";
        }

        static bool Declares(Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor a, string name)
            => a.Parameters.Any(p => string.Equals(p.Name, name, StringComparison.Ordinal));

        if (!routes.Any(a => Declares(a, "itemId") && Declares(a, EnableDirectStream)))
        {
            return "JellyShare: Jellyfin's PlaybackInfo action no longer declares itemId/" + EnableDirectStream + ". Placeholders "
                + "may play above a client's bitrate limit. Check for a JellyShare update.";
        }

        return null;
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        if (IsPlaybackInfo(context) && !await ApplyStreamLimitAsync(context).ConfigureAwait(false))
        {
            return;
        }

        if (IsPlaybackInfo(context) && DeclaresParameter(context, EnableDirectStream))
        {
            await ApplyBitrateRuleAsync(context).ConfigureAwait(false);
        }

        await next().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        if (context.Result is ObjectResult { Value: { } value } result)
        {
            if (Materialize(value) is { } materialized)
            {
                result.Value = value = materialized;
            }

            HideRelaySources(value, _relayBase.Current);
        }

        return next();
    }

    /// <summary>Blanks relay paths and disables direct play in the media sources of a response value.</summary>
    /// <param name="value">A PlaybackInfo response, item DTO, query result, list, or media source.</param>
    /// <param name="relayBase">The current relay base URL.</param>
    /// <returns>The number of sources changed.</returns>
    public static int HideRelaySources(object? value, string relayBase)
    {
        switch (value)
        {
            case null:
                return 0;
            case MediaSourceInfo source:
                return Hide(source, relayBase);
            case PlaybackInfoResponse playback:
                return HideAll(playback.MediaSources, relayBase);
            case LiveStreamResponse live:
                return Hide(live.MediaSource, relayBase);
            case BaseItemDto dto:
                return HideAll(dto.MediaSources, relayBase);
            case QueryResult<BaseItemDto> query:
                return HideEach(query.Items, relayBase);
            case IEnumerable<BaseItemDto> dtos:
                return HideEach(dtos, relayBase);
            case IEnumerable<MediaSourceInfo> sources:
                return HideAll(sources, relayBase);
            default:
                return 0;
        }
    }

    private static bool IsPlaybackInfo(FilterContext context)
        => string.Equals(context.ActionDescriptor.AttributeRouteInfo?.Template, PlaybackInfoTemplate, StringComparison.OrdinalIgnoreCase);

    private static bool DeclaresParameter(ActionExecutingContext context, string name)
        => context.ActionDescriptor.Parameters.Any(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    private static int HideEach(IEnumerable<BaseItemDto>? dtos, string relayBase)
        => dtos?.Sum(d => d is null ? 0 : HideAll(d.MediaSources, relayBase)) ?? 0;

    private static int HideAll(IEnumerable<MediaSourceInfo>? sources, string relayBase)
        => sources?.Sum(s => Hide(s, relayBase)) ?? 0;

    private static int Hide(MediaSourceInfo? source, string relayBase)
    {
        if (source is null || !RelayUrl.IsRelayUrl(source.Path, relayBase))
        {
            return 0;
        }

        source.Path = string.Empty;
        source.SupportsDirectPlay = false;
        return 1;
    }

    private static object? Body(ActionExecutingContext context)
        => context.ActionArguments.Values.FirstOrDefault(v => v is not null && v.GetType().GetProperty("DeviceProfile") is not null);

    // Jellyfin: maxStreamingBitrate (query) ?? body.MaxStreamingBitrate ?? profile.MaxStreamingBitrate, where
    // profile = body.DeviceProfile ?? the profile stored for the caller's device.
    private static int? RequestedBitrate(ActionExecutingContext context, object? body, int? storedProfileBitrate)
    {
        if (context.ActionArguments.TryGetValue("maxStreamingBitrate", out var query) && query is int fromQuery)
        {
            return fromQuery;
        }

        if (body?.GetType().GetProperty("MaxStreamingBitrate")?.GetValue(body) is int fromBody)
        {
            return fromBody;
        }

        if (body?.GetType().GetProperty("DeviceProfile")?.GetValue(body) is DeviceProfile profile)
        {
            return profile.MaxStreamingBitrate;
        }

        return storedProfileBitrate;
    }

    private static object? Materialize(object value)
        => value switch
        {
            // Lazy sequences (e.g. Jellyfin's SpecialFeatures/LocalTrailers return a Select) would produce fresh DTOs
            // when the serializer enumerates them again, so the changes must be made on a materialized copy.
            IEnumerable<BaseItemDto> dtos when value is not ICollection<BaseItemDto> and not IReadOnlyCollection<BaseItemDto>
                => dtos.ToArray(),
            IEnumerable<MediaSourceInfo> sources when value is not ICollection<MediaSourceInfo> and not IReadOnlyCollection<MediaSourceInfo>
                => sources.ToArray(),
            _ => null,
        };

    private static void SetBodyFlag(object? body, string property)
    {
        var prop = body?.GetType().GetProperty(property);
        if (prop is not null && prop.CanWrite && (prop.PropertyType == typeof(bool?) || prop.PropertyType == typeof(bool)))
        {
            prop.SetValue(body, false);
        }
    }

    // Returns false when the playback is refused (context.Result is set and the action must not run).
    private async Task<bool> ApplyStreamLimitAsync(ActionExecutingContext context)
    {
        if (!context.ActionArguments.TryGetValue("itemId", out var id) || id is not Guid itemId
            || _lookup.Find(itemId) is not { } placeholder
            || _index.Find(placeholder.CanonicalKey) is not { } entry)
        {
            return true;
        }

        var key = entry.CanonicalKey;
        var sources = SourceSelector.Order(entry, _config.Current.Peers, _index.PeerStates());
        var decision = StreamLimitGate.Decide(sources, key, _sessions);
        switch (decision.Kind)
        {
            case StreamDecisionKind.Refuse:
                _logger.LogInformation("JellyShare: refused {Key}: peer {Peer} allows {Max} stream(s)", key, decision.Peer?.Name, decision.Peer?.MaxStreams);
                context.Result = new OkObjectResult(new PlaybackInfoResponse { ErrorCode = PlaybackErrorCode.RateLimitExceeded });
                return false;
            case StreamDecisionKind.StopOldest when decision is { Peer: { } peer, StopKey: { } stopKey }:
                try
                {
                    await _stopper.StopTitleAsync(
                        stopKey,
                        $"Stopped: {peer.Name} allows {peer.MaxStreams} stream(s) at a time and another viewer started.",
                        context.HttpContext.RequestAborted).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "JellyShare: could not stop {Key} on peer {Peer}", stopKey, peer.Name);
                }
                finally
                {
                    // A client that cannot be reached must not keep the slot: the new viewer plays regardless.
                    _sessions.Release(stopKey);
                }

                return true;
            default:
                return true;
        }
    }

    private async Task ApplyBitrateRuleAsync(ActionExecutingContext context)
    {
        if (!context.ActionArguments.TryGetValue("itemId", out var id) || id is not Guid itemId)
        {
            return;
        }

        var placeholder = _lookup.Find(itemId);
        if (placeholder?.SourceBitrate is not { } sourceBitrate || sourceBitrate <= 0)
        {
            return;
        }

        var body = Body(context);
        var caller = await _callers.ResolveAsync(context.HttpContext).ConfigureAwait(false);
        var limits = caller is null
            ? new CallerLimits(null, null)
            : await _lookup.GetCallerLimitsAsync(caller).ConfigureAwait(false);
        var requested = RequestedBitrate(context, body, limits.StoredProfileBitrate);
        if (PlaybackBitrateRule.ForcedTranscodeLimit(sourceBitrate, JellyfinBitrateRules.EffectiveMaxBitrate(requested, limits.RemoteLimit)) is not { } limit)
        {
            return;
        }

        // Jellyfin copies query values over the body (`enableDirectPlay ??= dto.EnableDirectPlay`), so setting the
        // action arguments wins; the body is set too for completeness.
        if (DeclaresParameter(context, EnableDirectPlay))
        {
            context.ActionArguments[EnableDirectPlay] = false;
        }

        context.ActionArguments[EnableDirectStream] = false;
        SetBodyFlag(body, "EnableDirectPlay");
        SetBodyFlag(body, "EnableDirectStream");
        _logger.LogDebug(
            "JellyShare: transcoding {Key}: max bitrate {Max} is below source bitrate {Source}",
            placeholder.CanonicalKey,
            limit,
            sourceBitrate);
    }
}
