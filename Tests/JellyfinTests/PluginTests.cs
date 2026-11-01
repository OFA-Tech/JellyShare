using System.Text.RegularExpressions;
using JellyShare.Jellyfin;
using Xunit;

namespace JellyShare.JellyfinTests;

public class PluginTests
{
    [Fact]
    public void PluginId_MatchesBuildYamlGuid()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "build.yaml")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var yaml = File.ReadAllText(Path.Combine(dir!.FullName, "build.yaml"));
        var match = Regex.Match(yaml, @"^guid:\s*""?([0-9a-fA-F-]{36})""?\s*$", RegexOptions.Multiline);
        Assert.True(match.Success, "build.yaml has no guid line");
        Assert.Equal(Guid.Parse(match.Groups[1].Value), Plugin.PluginId);
    }

    [Fact]
    public void PluginName_IsJellyShare()
    {
        Assert.Equal("JellyShare", Plugin.PluginName);
    }
}
