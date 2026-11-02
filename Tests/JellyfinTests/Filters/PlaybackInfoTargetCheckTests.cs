using JellyShare.Jellyfin.Filters;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Routing;

namespace JellyShare.JellyfinTests.Filters;

public sealed class PlaybackInfoTargetCheckTests
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

        PlaybackInfoFilter.TargetWarning(actions).ShouldBeNull();
    }

    [Fact]
    public void TargetWarning_RouteRenamed_Warns()
    {
        var warning = PlaybackInfoFilter.TargetWarning(new[] { Action("Items/{itemId}/PlaybackInfoV2", "itemId", "enableDirectStream", "enableDirectPlay") });

        warning.ShouldNotBeNull().ShouldContain("PlaybackInfo", Case.Sensitive);
    }

    [Fact]
    public void TargetWarning_ParametersRenamed_Warns()
    {
        var warning = PlaybackInfoFilter.TargetWarning(new[] { Action("Items/{itemId}/PlaybackInfo", "id", "directStream", "directPlay") });

        warning.ShouldNotBeNull().ShouldContain("enableDirectStream", Case.Sensitive);
    }
}
