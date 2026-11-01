using JellyShare.Domain.Models.Configuration;

namespace JellyShare.UnitTests.Configuration;

public sealed class JellyShareSettingsTests
{
    [Fact]
    public void Defaults_MatchThePluginDefaults()
    {
        var settings = new JellyShareSettings();

        settings.Shares.ShouldBeEmpty();
        settings.Peers.ShouldBeEmpty();
        settings.MoviesPlaceholderPath.ShouldBe(string.Empty);
        settings.ShowsPlaceholderPath.ShouldBe(string.Empty);
        settings.SyncIntervalHours.ShouldBe(6);
        settings.PresenceSweepSeconds.ShouldBe(60);
        settings.MaxConcurrentTranscodes.ShouldBe(4);
        settings.RelayBaseOverride.ShouldBeNull();
        (new PeerShare().Enabled).ShouldBeTrue();
        (new PeerShare().MaxStreams).ShouldBe(2);
        (new RemotePeer().Enabled).ShouldBeTrue();
    }

    [Fact]
    public void ResolvePlaceholderRoots_EmptyPaths_DefaultUnderDataFolder()
    {
        var settings = new JellyShareSettings();

        JellyShareSettings.ResolvePlaceholderRoots(settings, Path.Combine("data", "plugin"));

        settings.MoviesPlaceholderPath.ShouldBe(Path.Combine("data", "plugin", "placeholders", "Movies"));
        settings.ShowsPlaceholderPath.ShouldBe(Path.Combine("data", "plugin", "placeholders", "Shows"));
    }

    [Fact]
    public void ResolvePlaceholderRoots_ConfiguredPaths_AreKept()
    {
        var settings = new JellyShareSettings { MoviesPlaceholderPath = "/custom/m", ShowsPlaceholderPath = "  " };

        JellyShareSettings.ResolvePlaceholderRoots(settings, "/d");

        settings.MoviesPlaceholderPath.ShouldBe("/custom/m");
        settings.ShowsPlaceholderPath.ShouldBe(Path.Combine("/d", "placeholders", "Shows"));
    }

    [Fact]
    public void ToString_NeverContainsTokensOrHashes()
    {
        var settings = new JellyShareSettings();
        settings.Shares.Add(new PeerShare { Name = "a", TokenHash = "deadbeefhash" });
        settings.Peers.Add(new RemotePeer { Name = "b", Token = "js1_secret-value" });

        settings.ToString().ShouldNotContain("deadbeefhash", Case.Sensitive);
        settings.ToString().ShouldNotContain("js1_secret-value", Case.Sensitive);
    }
}
