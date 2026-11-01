using System.Text.RegularExpressions;
using JellyShare.Jellyfin;

namespace JellyShare.JellyfinTests;

public sealed class PluginTests
{
    [Fact]
    public void PluginId_MatchesBuildYamlGuid()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "build.yaml")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull();
        var yaml = File.ReadAllText(Path.Combine(dir!.FullName, "build.yaml"));
        var match = Regex.Match(yaml, @"^guid:\s*""?([0-9a-fA-F-]{36})""?\s*$", RegexOptions.Multiline);
        match.Success.ShouldBeTrue("build.yaml has no guid line");
        Plugin.PluginId.ShouldBe(Guid.Parse(match.Groups[1].Value));
    }

    [Fact]
    public void PluginName_IsJellyShare()
    {
        Plugin.PluginName.ShouldBe("JellyShare");
    }
}
