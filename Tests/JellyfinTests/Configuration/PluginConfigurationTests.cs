using System.Xml;
using System.Xml.Serialization;
using JellyShare.Application.Features.Catalog;
using JellyShare.Domain.Enums;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Jellyfin.Configuration;

namespace JellyShare.JellyfinTests.Configuration;

public sealed class PluginConfigurationTests
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

        copy.Shares.Count.ShouldBe(2);
        copy.Peers.ShouldHaveSingleItem();
        copy.SyncIntervalHours.ShouldBe(12);
        copy.RelayBaseOverride.ShouldBe("http://127.0.0.1:8096");
        copy.MoviesPlaceholderPath.ShouldBe("/data/ph/Movies");
        copy.ShowsPlaceholderPath.ShouldBe("/data/ph/Shows");

        var s = copy.Shares[0];
        s.Id.ShouldBe(share.Id);
        s.Name.ShouldBe("alice");
        s.TokenHash.ShouldBe(hash);
        s.Enabled.ShouldBeFalse();
        s.MaxStreams.ShouldBe(5);
        s.MaxBitrate.ShouldBe(8_000_000);
        s.LibraryIds.ShouldBe(new[] { lib1, lib2 });
        copy.Shares[1].Enabled.ShouldBeTrue();
        copy.Shares[1].MaxBitrate.ShouldBeNull();
        copy.Shares[1].MaxStreams.ShouldBe(2);

        var p = copy.Peers[0];
        p.Id.ShouldBe(peer.Id);
        p.Name.ShouldBe("carol");
        p.BaseUrl.ShouldBe("https://carol.example");
        p.Token.ShouldBe("js1_remote-secret");
        p.Enabled.ShouldBeFalse();
        p.Priority.ShouldBe(3);

        xml.ShouldContain("js1_remote-secret", Case.Sensitive);
        xml.ShouldContain(hash, Case.Sensitive);
        xml.ShouldNotContain(token, Case.Sensitive);
    }

    [Fact]
    public void Config_RoundTrip_DoesNotDuplicateEmptyOrDefaultCollections()
    {
        var empty = new PluginConfiguration();
        RoundTrip(empty, out var copy);
        copy.Shares.ShouldBeEmpty();
        copy.Peers.ShouldBeEmpty();
        copy.SyncIntervalHours.ShouldBe(6);
        copy.RelayBaseOverride.ShouldBeNull();

        var cfg = new PluginConfiguration();
        cfg.Shares.Add(new PeerShare { Name = "a" });
        cfg.Peers.Add(new RemotePeer { Name = "b" });
        RoundTrip(cfg, out var copy2);
        copy2.Shares.ShouldHaveSingleItem();
        copy2.Peers.ShouldHaveSingleItem();
        copy2.Shares[0].LibraryIds.ShouldBeEmpty();
    }

    [Fact]
    public void ToString_NeverContainsTokensOrHashes()
    {
        var share = new PeerShare { Name = "a", TokenHash = "deadbeefhash" };
        var peer = new RemotePeer { Name = "b", Token = "js1_secret-value" };
        var cfg = new PluginConfiguration();
        cfg.Shares.Add(share);
        cfg.Peers.Add(peer);

        share.ToString().ShouldNotContain("deadbeefhash", Case.Sensitive);
        peer.ToString().ShouldNotContain("js1_secret-value", Case.Sensitive);
        cfg.ToString().ShouldNotContain("js1_secret-value", Case.Sensitive);
        cfg.ToString().ShouldNotContain("deadbeefhash", Case.Sensitive);
    }

    [Fact]
    public void ResolvePlaceholderRoots_EmptyPaths_DefaultUnderDataFolder()
    {
        var cfg = new PluginConfiguration();

        PluginConfiguration.ResolvePlaceholderRoots(cfg, Path.Combine("data", "plugin"));

        cfg.MoviesPlaceholderPath.ShouldBe(Path.Combine("data", "plugin", "placeholders", "Movies"));
        cfg.ShowsPlaceholderPath.ShouldBe(Path.Combine("data", "plugin", "placeholders", "Shows"));
        cfg.MusicPlaceholderPath.ShouldBe(Path.Combine("data", "plugin", "placeholders", "Music"));
        cfg.AudiobooksPlaceholderPath.ShouldBe(Path.Combine("data", "plugin", "placeholders", "Audiobooks"));
    }

    [Fact]
    public void MediaTypeSettings_RoundTripThroughXmlWithoutDuplicates()
    {
        var cfg = new PluginConfiguration { DefaultShareTypes = new[] { MediaType.Movies, MediaType.Music } };
        cfg.Shares.Add(new PeerShare { Name = "a", UseDefaultTypes = false, Types = { MediaType.Shows, MediaType.Audiobooks } });
        cfg.Shares.Add(new PeerShare { Name = "b", UseDefaultTypes = true });
        cfg.Peers.Add(new RemotePeer { Name = "c", AcceptAllTypes = false, AcceptTypes = { MediaType.Movies } });

        RoundTrip(cfg, out var copy);

        copy.DefaultShareTypes.ShouldBe(new[] { MediaType.Movies, MediaType.Music });
        copy.Shares[0].UseDefaultTypes.ShouldBe(false);
        copy.Shares[0].Types.ShouldBe(new[] { MediaType.Shows, MediaType.Audiobooks });
        copy.Shares[1].UseDefaultTypes.ShouldBe(true);
        copy.Shares[1].Types.ShouldBeEmpty();
        copy.Peers[0].AcceptAllTypes.ShouldBeFalse();
        copy.Peers[0].AcceptTypes.ShouldBe(new[] { MediaType.Movies });
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

        cfg.MoviesPlaceholderPath.ShouldBe("/custom/m");
        cfg.ShowsPlaceholderPath.ShouldBe(Path.Combine("/d", "placeholders", "Shows"));
    }

    [Fact]
    public void ToSettings_CopiesEveryValue_AndSharesTheLists()
    {
        var cfg = new PluginConfiguration
        {
            MoviesPlaceholderPath = "/m",
            ShowsPlaceholderPath = "/s",
            MusicPlaceholderPath = "/mu",
            AudiobooksPlaceholderPath = "/ab",
            DefaultShareTypes = new[] { MediaType.Movies },
            SyncIntervalHours = 12,
            PresenceSweepSeconds = 30,
            MaxConcurrentTranscodes = 2,
            RelayBaseOverride = "http://127.0.0.1:8096",
        };
        cfg.Shares.Add(new PeerShare { Name = "a" });
        cfg.Peers.Add(new RemotePeer { Name = "b" });

        var settings = cfg.ToSettings();

        settings.Shares.ShouldBeSameAs(cfg.Shares);
        settings.Peers.ShouldBeSameAs(cfg.Peers);
        settings.MoviesPlaceholderPath.ShouldBe("/m");
        settings.ShowsPlaceholderPath.ShouldBe("/s");
        settings.MusicPlaceholderPath.ShouldBe("/mu");
        settings.AudiobooksPlaceholderPath.ShouldBe("/ab");
        settings.DefaultShareTypes.ShouldBe(new[] { MediaType.Movies });
        settings.SyncIntervalHours.ShouldBe(12);
        settings.PresenceSweepSeconds.ShouldBe(30);
        settings.MaxConcurrentTranscodes.ShouldBe(2);
        settings.RelayBaseOverride.ShouldBe("http://127.0.0.1:8096");
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

        reader.Shares.ShouldHaveSingleItem();
        cfg.Shares.Count.ShouldBe(2);
        cfg.SyncIntervalHours.ShouldBe(24);
        cfg.RelayBaseOverride.ShouldBe("http://localhost:8096");
        cfg.ToSettings().Shares.Count.ShouldBe(2);
    }

    [Fact]
    public void ToSettings_And_Apply_RoundTripEveryProperty_WithNonDefaultValues()
    {
        var properties = typeof(JellyShareSettings).GetProperties().Where(p => p.DeclaringType == typeof(JellyShareSettings)).ToArray();
        properties.ShouldNotBeEmpty();

        // Every property gets a value different from its default, so a property missing from the mapping fails here.
        var original = new JellyShareSettings();
        var expected = new Dictionary<string, object?>();
        foreach (var property in properties)
        {
            var defaultValue = property.GetValue(new JellyShareSettings());
            var value = NonDefault(property.PropertyType, property.Name);
            value.ShouldNotBe(defaultValue);
            property.SetValue(original, value);
            expected[property.Name] = value;
        }

        var cfg = new PluginConfiguration();
        cfg.Apply(original);
        var back = cfg.ToSettings();

        foreach (var property in properties)
        {
            typeof(PluginConfiguration).GetProperty(property.Name)!.GetValue(cfg).ShouldBe(expected[property.Name]);
            property.GetValue(back).ShouldBe(expected[property.Name]);
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

        if (type == typeof(bool) || type == typeof(bool?))
        {
            return true;
        }

        if (type == typeof(List<MediaType>))
        {
            return new List<MediaType> { MediaType.Music };
        }

        if (type == typeof(MediaType[]))
        {
            return new[] { MediaType.Music };
        }

        throw new NotSupportedException("Add a non-default value for " + type + " (" + name + ") to this test.");
    }
    [Fact]
    public void PluginConfiguration_HasTheSamePublicSettingsAsJellyShareSettings()
    {
        static string[] Props(Type t) => t.GetProperties().Where(p => p.DeclaringType == t).Select(p => p.Name + ":" + p.PropertyType).ToArray();

        Props(typeof(PluginConfiguration)).ShouldBe(Props(typeof(JellyShareSettings)));
    }

    private static string RoundTrip(PluginConfiguration cfg, out PluginConfiguration copy)
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var writer = new StringWriter();
        serializer.Serialize(writer, cfg);
        var xml = writer.ToString();
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        copy = (PluginConfiguration)serializer.Deserialize(reader)!;
        return xml;
    }
}
