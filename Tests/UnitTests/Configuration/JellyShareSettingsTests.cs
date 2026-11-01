using JellyShare.Domain.Models.Configuration;
using Xunit;

namespace JellyShare.UnitTests.Configuration;

public sealed class JellyShareSettingsTests
{
    [Fact]
    public void Defaults_MatchThePluginDefaults()
    {
        var settings = new JellyShareSettings();

        Assert.Empty(settings.Shares);
        Assert.Empty(settings.Peers);
        Assert.Equal(string.Empty, settings.MoviesPlaceholderPath);
        Assert.Equal(string.Empty, settings.ShowsPlaceholderPath);
        Assert.Equal(6, settings.SyncIntervalHours);
        Assert.Equal(60, settings.PresenceSweepSeconds);
        Assert.Equal(4, settings.MaxConcurrentTranscodes);
        Assert.Null(settings.RelayBaseOverride);
        Assert.True(new PeerShare().Enabled);
        Assert.Equal(2, new PeerShare().MaxStreams);
        Assert.True(new RemotePeer().Enabled);
    }

    [Fact]
    public void ResolvePlaceholderRoots_EmptyPaths_DefaultUnderDataFolder()
    {
        var settings = new JellyShareSettings();

        JellyShareSettings.ResolvePlaceholderRoots(settings, Path.Combine("data", "plugin"));

        Assert.Equal(Path.Combine("data", "plugin", "placeholders", "Movies"), settings.MoviesPlaceholderPath);
        Assert.Equal(Path.Combine("data", "plugin", "placeholders", "Shows"), settings.ShowsPlaceholderPath);
    }

    [Fact]
    public void ResolvePlaceholderRoots_ConfiguredPaths_AreKept()
    {
        var settings = new JellyShareSettings { MoviesPlaceholderPath = "/custom/m", ShowsPlaceholderPath = "  " };

        JellyShareSettings.ResolvePlaceholderRoots(settings, "/d");

        Assert.Equal("/custom/m", settings.MoviesPlaceholderPath);
        Assert.Equal(Path.Combine("/d", "placeholders", "Shows"), settings.ShowsPlaceholderPath);
    }

    [Fact]
    public void ToString_NeverContainsTokensOrHashes()
    {
        var settings = new JellyShareSettings();
        settings.Shares.Add(new PeerShare { Name = "a", TokenHash = "deadbeefhash" });
        settings.Peers.Add(new RemotePeer { Name = "b", Token = "js1_secret-value" });

        Assert.DoesNotContain("deadbeefhash", settings.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("js1_secret-value", settings.ToString(), StringComparison.Ordinal);
    }
}
