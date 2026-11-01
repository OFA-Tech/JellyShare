using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;
using JellyShare.Application.Features.Catalog;
using JellyShare.Domain.Models.Configuration;
using Xunit;

namespace JellyShare.UnitTests.Catalog;

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
}

