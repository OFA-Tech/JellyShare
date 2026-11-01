using JellyShare.Domain.Enums;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Peers;
using JellyShare.Domain.Models.Protocol;
using JellyShare.Domain.Models.Requests.Peers;
using JellyShare.UnitTests.Fakes;
using Xunit;

namespace JellyShare.UnitTests.Peers;

public sealed class TestPeerHandlerTests : IDisposable
{
    private const string Token = "js1_PEER-secret-token";

    private readonly AdminHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static CatalogResponse Catalog(int items)
        => new(ProtocolInfo.Version, "Bob's server", "etag", Enumerable.Range(0, items).Select(i => TestItems.Movie("m" + i, tmdb: i.ToString(System.Globalization.CultureInfo.InvariantCulture), id: "m" + i)).ToList());

    [Fact]
    public async Task InvalidUrl_IsAFailedTest_NotAnError()
    {
        var result = await _h.Send(new TestPeerQuery("nope", Token, null));

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.Ok);
        Assert.StartsWith("The URL is not valid.", result.Value.Error, StringComparison.Ordinal);
        Assert.Empty(_h.Peers.PingedUrls);
    }

    [Fact]
    public async Task MissingToken_IsAFailedTest()
    {
        var result = await _h.Send(new TestPeerQuery("http://bob:8096", " ", null));

        Assert.Equal("Enter the token from the invitation.", result.Value.Error);
    }

    [Fact]
    public async Task TokenWithControlCharacters_IsAFailedTest()
    {
        var result = await _h.Send(new TestPeerQuery("http://bob:8096", "a\nb", null));

        Assert.Equal("The token contains line breaks or other control characters. Paste only the token.", result.Value.Error);
    }

    [Fact]
    public async Task SavedPeer_WithAChangedAddress_NeverSendsTheStoredToken()
    {
        var saved = new RemotePeer { Id = Guid.NewGuid(), Name = "bob", BaseUrl = "http://bob:8096", Token = Token };
        _h.Config.Settings.Peers.Add(saved);

        var result = await _h.Send(new TestPeerQuery("http://evil:8096", null, saved.Id));

        Assert.False(result.Value.Ok);
        Assert.StartsWith("The address was changed.", result.Value.Error, StringComparison.Ordinal);
        Assert.Empty(_h.Peers.PingedUrls);
    }

    [Fact]
    public async Task SavedPeer_UsesTheStoredToken_ForItsAddress()
    {
        var saved = new RemotePeer { Id = Guid.NewGuid(), Name = "bob", BaseUrl = "http://bob:8096", Token = Token };
        _h.Config.Settings.Peers.Add(saved);
        _h.Peers.Catalogs[saved.Id] = _ => new PeerFetchResult(true, false, Catalog(2), null);

        var result = await _h.Send(new TestPeerQuery("http://bob:8096/", null, saved.Id));

        Assert.True(result.Value.Ok);
        Assert.Equal(2, result.Value.Titles);
    }

    [Fact]
    public async Task FailedPing_IsExplained_AndRedacted()
    {
        _h.Peers.Pings = _ => new PeerPingResult(false, null, null, PingFailure.Unreachable, "refused " + Token);

        var result = await _h.Send(new TestPeerQuery("http://bob:8096", Token, null));

        Assert.False(result.Value.Ok);
        Assert.Equal("Could not reach the server: refused ***", result.Value.Error);
    }

    [Fact]
    public async Task FailedCatalog_IsExplained()
    {
        var result = await _h.Send(new TestPeerQuery("http://bob:8096", Token, null));

        Assert.False(result.Value.Ok);
        Assert.Equal("The catalog request failed: HTTP 404", result.Value.Error);
    }

    [Fact]
    public async Task Success_ReportsServerProtocolAndTitles()
    {
        _h.Peers.Catalogs[Guid.Empty] = _ => new PeerFetchResult(true, false, Catalog(3), null);

        var result = await _h.Send(new TestPeerQuery("http://bob:8096", Token, null));

        Assert.True(result.Value.Ok);
        Assert.Equal("peer", result.Value.ServerName);
        Assert.Equal(ProtocolInfo.Version, result.Value.ProtocolVersion);
        Assert.Equal(3, result.Value.Titles);
        Assert.Null(result.Value.Error);
    }
}
