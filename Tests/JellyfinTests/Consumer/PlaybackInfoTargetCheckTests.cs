using JellyShare.Jellyfin.Consumer;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace JellyShare.JellyfinTests.Consumer;

public class PlaybackInfoTargetCheckTests
{
    private static ActionDescriptor Action(string template, params string[] parameters)
        => new()
        {
            AttributeRouteInfo = new AttributeRouteInfo { Template = template },
            Parameters = parameters.Select(p => new ParameterDescriptor { Name = p, ParameterType = typeof(object) }).ToList(),
        };

    [Fact]
    public void TargetWarning_JellyfinShape_IsNull()
    {
        var actions = new[]
        {
            Action("Items/{itemId}/PlaybackInfo", "itemId", "userId"),
            Action("Items/{itemId}/PlaybackInfo", "itemId", "maxStreamingBitrate", "enableDirectPlay", "enableDirectStream", "playbackInfoDto"),
            Action("Items/{itemId}"),
        };

        Assert.Null(PlaybackInfoFilter.TargetWarning(actions));
    }

    [Fact]
    public void TargetWarning_RouteRenamed_Warns()
    {
        var warning = PlaybackInfoFilter.TargetWarning(new[] { Action("Items/{itemId}/PlaybackInfoV2", "itemId", "enableDirectStream", "enableDirectPlay") });

        Assert.Contains("PlaybackInfo", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void TargetWarning_ParametersRenamed_Warns()
    {
        var warning = PlaybackInfoFilter.TargetWarning(new[] { Action("Items/{itemId}/PlaybackInfo", "id", "directStream", "directPlay") });

        Assert.Contains("enableDirectStream", warning, StringComparison.Ordinal);
    }
}
