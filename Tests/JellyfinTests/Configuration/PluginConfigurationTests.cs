using System.Xml.Serialization;
using JellyShare.Application.Features.Catalog;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Jellyfin.Configuration;
using Xunit;

namespace JellyShare.JellyfinTests.Configuration;

public class PluginConfigurationTests
{
    private readonly ShareTokenService _service = new();

    [Fact]
    public void Config_RoundTripsThroughXmlSerializer()
    {
        var (token, hash) = _service.Create();
        var lib1 = Guid.NewGuid();
        var lib2 = Guid.NewGuid();
        var cfg = new PluginConfiguration
        {
            SyncIntervalHours = 12,
            RelayBaseOverride = "http://127.0.0.1:8096",
            MoviesPlaceholderPath = "/data/ph/Movies",
            ShowsPlaceholderPath = "/data/ph/Shows",
        };
        var share = new PeerShare
        {
            Id = Guid.NewGuid(),
            Name = "alice",
            TokenHash = hash,
            Enabled = false,
            MaxStreams = 5,
            MaxBitrate = 8_000_000,
        };
        share.LibraryIds.Add(lib1);
        share.LibraryIds.Add(lib2);
        cfg.Shares.Add(share);
        cfg.Shares.Add(new PeerShare { Id = Guid.NewGuid(), Name = "bob", TokenHash = hash });
        var peer = new RemotePeer
        {
            Id = Guid.NewGuid(),
            Name = "carol",
            BaseUrl = "https://carol.example",
            Token = "js1_remote-secret",
            Enabled = false,
            Priority = 3,
        };
        cfg.Peers.Add(peer);

        var xml = RoundTrip(cfg, out var copy);

        Assert.Equal(2, copy.Shares.Count);
        Assert.Single(copy.Peers);
        Assert.Equal(12, copy.SyncIntervalHours);
        Assert.Equal("http://127.0.0.1:8096", copy.RelayBaseOverride);
        Assert.Equal("/data/ph/Movies", copy.MoviesPlaceholderPath);
        Assert.Equal("/data/ph/Shows", copy.ShowsPlaceholderPath);

        var s = copy.Shares[0];
        Assert.Equal(share.Id, s.Id);
        Assert.Equal("alice", s.Name);
        Assert.Equal(hash, s.TokenHash);
        Assert.False(s.Enabled);
        Assert.Equal(5, s.MaxStreams);
        Assert.Equal(8_000_000, s.MaxBitrate);
        Assert.Equal(new[] { lib1, lib2 }, s.LibraryIds);
        Assert.True(copy.Shares[1].Enabled);
        Assert.Null(copy.Shares[1].MaxBitrate);
        Assert.Equal(2, copy.Shares[1].MaxStreams);

        var p = copy.Peers[0];
        Assert.Equal(peer.Id, p.Id);
        Assert.Equal("carol", p.Name);
        Assert.Equal("https://carol.example", p.BaseUrl);
        Assert.Equal("js1_remote-secret", p.Token);
        Assert.False(p.Enabled);
        Assert.Equal(3, p.Priority);

        Assert.Contains("js1_remote-secret", xml, StringComparison.Ordinal);
        Assert.Contains(hash, xml, StringComparison.Ordinal);
        Assert.DoesNotContain(token, xml, StringComparison.Ordinal);
    }

    [Fact]
    public void Config_RoundTrip_DoesNotDuplicateEmptyOrDefaultCollections()
    {
        var empty = new PluginConfiguration();
        RoundTrip(empty, out var copy);
        Assert.Empty(copy.Shares);
        Assert.Empty(copy.Peers);
        Assert.Equal(6, copy.SyncIntervalHours);
        Assert.Null(copy.RelayBaseOverride);

        var cfg = new PluginConfiguration();
        cfg.Shares.Add(new PeerShare { Name = "a" });
        cfg.Peers.Add(new RemotePeer { Name = "b" });
        RoundTrip(cfg, out var copy2);
        Assert.Single(copy2.Shares);
        Assert.Single(copy2.Peers);
        Assert.Empty(copy2.Shares[0].LibraryIds);
    }

    [Fact]
    public void ToString_NeverContainsTokensOrHashes()
    {
        var share = new PeerShare { Name = "a", TokenHash = "deadbeefhash" };
        var peer = new RemotePeer { Name = "b", Token = "js1_secret-value" };
        var cfg = new PluginConfiguration();
        cfg.Shares.Add(share);
        cfg.Peers.Add(peer);

        Assert.DoesNotContain("deadbeefhash", share.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("js1_secret-value", peer.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("js1_secret-value", cfg.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("deadbeefhash", cfg.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ResolvePlaceholderRoots_EmptyPaths_DefaultUnderDataFolder()
    {
        var cfg = new PluginConfiguration();

        PluginConfiguration.ResolvePlaceholderRoots(cfg, Path.Combine("data", "plugin"));

        Assert.Equal(Path.Combine("data", "plugin", "placeholders", "Movies"), cfg.MoviesPlaceholderPath);
        Assert.Equal(Path.Combine("data", "plugin", "placeholders", "Shows"), cfg.ShowsPlaceholderPath);
    }

    [Fact]
    public void ResolvePlaceholderRoots_ConfiguredPaths_AreKept()
    {
        var cfg = new PluginConfiguration
        {
            MoviesPlaceholderPath = "/custom/m",
            ShowsPlaceholderPath = "  ",
        };

        PluginConfiguration.ResolvePlaceholderRoots(cfg, "/d");

        Assert.Equal("/custom/m", cfg.MoviesPlaceholderPath);
        Assert.Equal(Path.Combine("/d", "placeholders", "Shows"), cfg.ShowsPlaceholderPath);
    }

    [Fact]
    public void ToSettings_CopiesEveryValue_AndSharesTheLists()
    {
        var cfg = new PluginConfiguration
        {
            MoviesPlaceholderPath = "/m",
            ShowsPlaceholderPath = "/s",
            SyncIntervalHours = 12,
            PresenceSweepSeconds = 30,
            MaxConcurrentTranscodes = 2,
            RelayBaseOverride = "http://127.0.0.1:8096",
        };
        cfg.Shares.Add(new PeerShare { Name = "a" });
        cfg.Peers.Add(new RemotePeer { Name = "b" });

        var settings = cfg.ToSettings();

        Assert.Same(cfg.Shares, settings.Shares);
        Assert.Same(cfg.Peers, settings.Peers);
        Assert.Equal("/m", settings.MoviesPlaceholderPath);
        Assert.Equal("/s", settings.ShowsPlaceholderPath);
        Assert.Equal(12, settings.SyncIntervalHours);
        Assert.Equal(30, settings.PresenceSweepSeconds);
        Assert.Equal(2, settings.MaxConcurrentTranscodes);
        Assert.Equal("http://127.0.0.1:8096", settings.RelayBaseOverride);
    }

    [Fact]
    public void Apply_CopyOnWrite_ReadersKeepTheirSnapshot()
    {
        var cfg = new PluginConfiguration();
        cfg.Shares.Add(new PeerShare { Name = "old" });
        var reader = cfg.ToSettings();

        var writer = cfg.ToSettings();
        writer.Shares = new List<PeerShare>(writer.Shares) { new() { Name = "new" } };
        writer.SyncIntervalHours = 24;
        writer.RelayBaseOverride = "http://localhost:8096";
        cfg.Apply(writer);

        Assert.Single(reader.Shares);
        Assert.Equal(2, cfg.Shares.Count);
        Assert.Equal(24, cfg.SyncIntervalHours);
        Assert.Equal("http://localhost:8096", cfg.RelayBaseOverride);
        Assert.Equal(2, cfg.ToSettings().Shares.Count);
    }

    [Fact]
    public void ToSettings_And_Apply_RoundTripEveryProperty_WithNonDefaultValues()
    {
        var properties = typeof(JellyShareSettings).GetProperties().Where(p => p.DeclaringType == typeof(JellyShareSettings)).ToArray();
        Assert.NotEmpty(properties);

        // Every property gets a value different from its default, so a property missing from the mapping fails here.
        var original = new JellyShareSettings();
        var expected = new Dictionary<string, object?>();
        foreach (var property in properties)
        {
            var defaultValue = property.GetValue(new JellyShareSettings());
            var value = NonDefault(property.PropertyType, property.Name);
            Assert.NotEqual(defaultValue, value);
            property.SetValue(original, value);
            expected[property.Name] = value;
        }

        var cfg = new PluginConfiguration();
        cfg.Apply(original);
        var back = cfg.ToSettings();

        foreach (var property in properties)
        {
            Assert.Equal(expected[property.Name], typeof(PluginConfiguration).GetProperty(property.Name)!.GetValue(cfg));
            Assert.Equal(expected[property.Name], property.GetValue(back));
        }
    }

    private static object? NonDefault(Type type, string name)
    {
        if (type == typeof(int))
        {
            return 777;
        }

        if (type == typeof(string))
        {
            return "/non-default/" + name;
        }

        if (type == typeof(List<PeerShare>))
        {
            return new List<PeerShare> { new() { Name = name } };
        }

        if (type == typeof(List<RemotePeer>))
        {
            return new List<RemotePeer> { new() { Name = name } };
        }

        throw new NotSupportedException("Add a non-default value for " + type + " (" + name + ") to this test.");
    }
    [Fact]
    public void PluginConfiguration_HasTheSamePublicSettingsAsJellyShareSettings()
    {
        static string[] Props(Type t) => t.GetProperties().Where(p => p.DeclaringType == t).Select(p => p.Name + ":" + p.PropertyType).ToArray();

        Assert.Equal(Props(typeof(JellyShareSettings)), Props(typeof(PluginConfiguration)));
    }

    private static string RoundTrip(PluginConfiguration cfg, out PluginConfiguration copy)
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var writer = new StringWriter();
        serializer.Serialize(writer, cfg);
        var xml = writer.ToString();
        using var reader = new StringReader(xml);
        copy = (PluginConfiguration)serializer.Deserialize(reader)!;
        return xml;
    }
}
