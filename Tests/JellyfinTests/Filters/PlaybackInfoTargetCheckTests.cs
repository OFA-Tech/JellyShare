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

    // Jellyfin 12.4's shape (parameter names from Jellyfin.Api: MediaInfo, Audio, UniversalAudio, DynamicHls controllers).
    private static List<ActionDescriptor> JellyfinActions() =>
    [
        Action("Items/{itemId}/PlaybackInfo", "itemId", "userId"),
        Action("Items/{itemId}/PlaybackInfo", "itemId", "maxStreamingBitrate", "enableDirectPlay", "enableDirectStream", "mediaSourceId", "playbackInfoDto"),
        Action("Items/{itemId}"),
        Action("Audio/{itemId}/stream", "itemId", "static", "mediaSourceId"),
        Action("Audio/{itemId}/stream.{container}", "itemId", "container", "mediaSourceId"),
        Action("Audio/{itemId}/universal", "itemId", "mediaSourceId", "enableRedirection"),
        Action("Audio/{itemId}/main.m3u8", "itemId", "mediaSourceId"),
        Action("Audio/{itemId}/master.m3u8", "itemId", "mediaSourceId"),
    ];

    [Fact]
    public void TargetWarning_JellyfinShape_IsNull()
        => PlaybackInfoFilter.TargetWarning(JellyfinActions()).ShouldBeNull();

    [Theory]
    [InlineData("Audio/{itemId}/stream")]
    [InlineData("Audio/{itemId}/stream.{container}")]
    [InlineData("Audio/{itemId}/universal")]
    [InlineData("Audio/{itemId}/main.m3u8")]
    [InlineData("Audio/{itemId}/master.m3u8")]
    public void TargetWarning_AudioRouteMissing_Warns(string template)
    {
        var actions = JellyfinActions().Where(a => a.AttributeRouteInfo!.Template != template);

        PlaybackInfoFilter.TargetWarning(actions).ShouldNotBeNull().ShouldContain(template, Case.Sensitive);
    }

    [Theory]
    [InlineData("Audio/{itemId}/stream", "mediaSourceId")]
    [InlineData("Audio/{itemId}/universal", "itemId")]
    [InlineData("Audio/{itemId}/universal", "enableRedirection")]
    [InlineData("Items/{itemId}/PlaybackInfo", "mediaSourceId")]
    public void TargetWarning_AudioParameterRenamed_Warns(string template, string parameter)
    {
        var actions = JellyfinActions()
            .Select(a => a.AttributeRouteInfo!.Template != template
                ? a
                : Action(template, a.Parameters.Select(p => p.Name == parameter ? p.Name + "V2" : p.Name).ToArray()));

        PlaybackInfoFilter.TargetWarning(actions).ShouldNotBeNull().ShouldContain(template, Case.Sensitive);
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
