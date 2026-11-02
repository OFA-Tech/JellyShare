using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;
using JellyShare.Application.Features.Catalog;
using JellyShare.Domain.Models.Configuration;

namespace JellyShare.UnitTests.Catalog;

public sealed class ShareTokenServiceTests
{
    private readonly ShareTokenService _service = new();

    [Fact]
    public void Create_TokenHasPrefixAndHashMatches()
    {
        var (token, hash) = _service.Create();

        token.ShouldStartWith("js1_", Case.Sensitive);
        var body = token.Substring(4);
        body.Length.ShouldBe(43);
        body.ShouldNotContain('=');
        body.ShouldNotContain('+');
        body.ShouldNotContain('/');

        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        hash.ShouldBe(expected);
        hash.Length.ShouldBe(64);

        _service.Create().Token.ShouldNotBe(token);
    }

    [Fact]
    public void Verify_CorrectToken_ReturnsShare()
    {
        var (token, hash) = _service.Create();
        var other = _service.Create();
        var mine = new PeerShare { Id = Guid.NewGuid(), Name = "mine", TokenHash = hash };
        var theirs = new PeerShare { Id = Guid.NewGuid(), Name = "theirs", TokenHash = other.Hash };

        _service.Verify(token, new[] { theirs, mine }).ShouldBeSameAs(mine);
    }

    [Fact]
    public void Verify_WrongToken_ReturnsNull()
    {
        var (_, hash) = _service.Create();
        var share = new PeerShare { TokenHash = hash };

        _service.Verify(_service.Create().Token, new[] { share }).ShouldBeNull();
    }

    [Fact]
    public void Verify_DisabledShare_ReturnsNull()
    {
        var (token, hash) = _service.Create();
        var share = new PeerShare { TokenHash = hash, Enabled = false };

        _service.Verify(token, new[] { share }).ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Verify_NullOrEmpty_ReturnsNull(string? presented)
    {
        var (_, hash) = _service.Create();
        var share = new PeerShare { TokenHash = hash };

        _service.Verify(presented, new[] { share }).ShouldBeNull();
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

        _service.Verify(token, shares).ShouldBeSameAs(shares[3]);
    }
}

