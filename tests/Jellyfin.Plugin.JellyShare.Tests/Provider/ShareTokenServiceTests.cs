using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;
using Jellyfin.Plugin.JellyShare.Configuration;
using Jellyfin.Plugin.JellyShare.Provider;
using Xunit;

namespace Jellyfin.Plugin.JellyShare.Tests.Provider;

public class ShareTokenServiceTests
{
    private readonly ShareTokenService _service = new();

    [Fact]
    public void Create_TokenHasPrefixAndHashMatches()
    {
        var (token, hash) = _service.Create();

        Assert.StartsWith("js1_", token, StringComparison.Ordinal);
        var body = token.Substring(4);
        Assert.Equal(43, body.Length);
        Assert.DoesNotContain('=', body);
        Assert.DoesNotContain('+', body);
        Assert.DoesNotContain('/', body);

        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        Assert.Equal(expected, hash);
        Assert.Equal(64, hash.Length);

        Assert.NotEqual(token, _service.Create().Token);
    }

    [Fact]
    public void Verify_CorrectToken_ReturnsShare()
    {
        var (token, hash) = _service.Create();
        var other = _service.Create();
        var mine = new PeerShare { Id = Guid.NewGuid(), Name = "mine", TokenHash = hash };
        var theirs = new PeerShare { Id = Guid.NewGuid(), Name = "theirs", TokenHash = other.Hash };

        Assert.Same(mine, _service.Verify(token, new[] { theirs, mine }));
    }

    [Fact]
    public void Verify_WrongToken_ReturnsNull()
    {
        var (_, hash) = _service.Create();
        var share = new PeerShare { TokenHash = hash };

        Assert.Null(_service.Verify(_service.Create().Token, new[] { share }));
    }

    [Fact]
    public void Verify_DisabledShare_ReturnsNull()
    {
        var (token, hash) = _service.Create();
        var share = new PeerShare { TokenHash = hash, Enabled = false };

        Assert.Null(_service.Verify(token, new[] { share }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Verify_NullOrEmpty_ReturnsNull(string? presented)
    {
        var (_, hash) = _service.Create();
        var share = new PeerShare { TokenHash = hash };

        Assert.Null(_service.Verify(presented, new[] { share }));
    }

    [Fact]
    public void Verify_MalformedStoredHash_DoesNotThrowAndStillMatchesOthers()
    {
        var (token, hash) = _service.Create();
        var shares = new[]
        {
            new PeerShare { TokenHash = null! },
            new PeerShare { TokenHash = "not-hex" },
            new PeerShare { TokenHash = "abcd" },
            new PeerShare { TokenHash = hash },
        };

        Assert.Same(shares[3], _service.Verify(token, shares));
    }

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
