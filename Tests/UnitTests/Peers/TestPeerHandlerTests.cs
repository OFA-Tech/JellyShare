using JellyShare.Domain.Enums;
using JellyShare.Application.Features.Peers;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Peers;
using JellyShare.Domain.Models.Protocol;
using JellyShare.Domain.Models.Requests.Peers;
using JellyShare.UnitTests.Fakes;

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

        result.IsSuccess.ShouldBeTrue();
        result.Value.Ok.ShouldBeFalse();
        result.Value.Error.ShouldStartWith("The URL is not valid.", Case.Sensitive);
        _h.Peers.PingedUrls.ShouldBeEmpty();
    }

    [Fact]
    public async Task MissingToken_IsAFailedTest()
    {
        var result = await _h.Send(new TestPeerQuery("http://bob:8096", " ", null));

        result.Value.Error.ShouldBe("Enter the token from the invitation.");
    }

    [Fact]
    public async Task TokenWithControlCharacters_IsAFailedTest()
    {
        var result = await _h.Send(new TestPeerQuery("http://bob:8096", "a\nb", null));

        result.Value.Error.ShouldBe("The token contains line breaks or other control characters. Paste only the token.");
    }

    [Fact]
    public async Task SavedPeer_WithAChangedAddress_NeverSendsTheStoredToken()
    {
        var saved = new RemotePeer { Id = Guid.NewGuid(), Name = "bob", BaseUrl = "http://bob:8096", Token = Token };
        _h.Config.Settings.Peers.Add(saved);

        var result = await _h.Send(new TestPeerQuery("http://evil:8096", null, saved.Id));

        result.Value.Ok.ShouldBeFalse();
        result.Value.Error.ShouldStartWith("The address was changed.", Case.Sensitive);
        _h.Peers.PingedUrls.ShouldBeEmpty();
    }

    [Fact]
    public async Task SavedPeer_UsesTheStoredToken_ForItsAddress()
    {
        var saved = new RemotePeer { Id = Guid.NewGuid(), Name = "bob", BaseUrl = "http://bob:8096", Token = Token };
        _h.Config.Settings.Peers.Add(saved);
        _h.Peers.Catalogs[saved.Id] = _ => new PeerFetchResult(true, false, Catalog(2), null);

        var result = await _h.Send(new TestPeerQuery("http://bob:8096/", null, saved.Id));

        result.Value.Ok.ShouldBeTrue();
        result.Value.Titles.ShouldBe(2);
    }

    [Fact]
    public async Task FailedPing_IsExplained_AndRedacted()
    {
        _h.Peers.Pings = _ => new PeerPingResult(false, null, null, PingFailure.Unreachable, "refused " + Token);

        var result = await _h.Send(new TestPeerQuery("http://bob:8096", Token, null));

        result.Value.Ok.ShouldBeFalse();
        result.Value.Error.ShouldBe("Could not reach the server: refused ***");
    }

    [Fact]
    public async Task FailedCatalog_IsExplained()
    {
        var result = await _h.Send(new TestPeerQuery("http://bob:8096", Token, null));

        result.Value.Ok.ShouldBeFalse();
        result.Value.Error.ShouldBe("The catalog request failed: HTTP 404");
    }

    private const string Password = "p@ss-Secret-1";

    private static TestPeerQuery AccountQuery(string url = "http://bob:8096", string? password = Password, Guid? peerId = null)
        => new(url, null, peerId, PeerKind.Account, "carol", password);

    [Fact]
    public async Task TestAccountPeer_SignsOutAfterTest()
    {
        _h.Peers.DefaultCatalog = _ => new PeerFetchResult(true, false, Catalog(2), null);

        var result = await _h.Send(AccountQuery());
        var fetched = _h.Peers.FetchedPeers.ShouldHaveSingleItem();

        result.Value.Ok.ShouldBeTrue();
        result.Value.Titles.ShouldBe(2);
        result.Value.ServerName.ShouldBe("Bob's server");
        var signIn = _h.Accounts.SignInCalls.ShouldHaveSingleItem();
        signIn.Password.ShouldBe(Password);
        signIn.PeerId.ShouldNotBe(Guid.Empty);
        var signOut = _h.Accounts.SignOutCalls.ShouldHaveSingleItem();
        signOut.AccessToken.ShouldBe("access-token-1");
        signOut.PeerId.ShouldBe(signIn.PeerId);
        fetched.Kind.ShouldBe(PeerKind.Account);
        fetched.Token.ShouldBe("access-token-1");
        fetched.AccountUserId.ShouldBe("uid-9");
        fetched.Id.ShouldBe(signIn.PeerId);
        _h.Peers.PingedUrls.ShouldBeEmpty();
    }

    [Fact]
    public async Task TestAccountPeer_FetchFails_StillSignsOut_AndLeaksNothing()
    {
        var result = await _h.Send(AccountQuery());

        result.Value.Ok.ShouldBeFalse();
        result.Value.Error!.ShouldNotContain(Password, Case.Sensitive);
        result.Value.Error!.ShouldNotContain("access-token-1", Case.Sensitive);
        _h.Accounts.SignOutCalls.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task TestAccountPeer_SignInFails_IsAFailedTest_WithoutSecrets()
    {
        _h.Accounts.SignIns = (_, password) => new AccountSignInResult(false, null, null, null, "sign-in failed: wrong " + password);

        var result = await _h.Send(AccountQuery());

        result.IsSuccess.ShouldBeTrue();
        result.Value.Ok.ShouldBeFalse();
        result.Value.Error.ShouldStartWith("sign-in failed", Case.Sensitive);
        result.Value.Error.ShouldNotContain(Password, Case.Sensitive);
        _h.Accounts.SignOutCalls.ShouldBeEmpty();
        _h.Peers.Fetches.ShouldBeEmpty();
    }

    [Fact]
    public async Task TestAccountPeer_UsesSavedTokenForSameUrl()
    {
        var saved = new RemotePeer
        {
            Id = Guid.NewGuid(), Name = "bob", BaseUrl = "http://bob:8096", Token = "saved-access", Kind = PeerKind.Account, Username = "carol", AccountUserId = "uid-1",
        };
        _h.Config.Settings.Peers.Add(saved);
        _h.Peers.Catalogs[saved.Id] = _ => new PeerFetchResult(true, false, Catalog(2), null);

        var result = await _h.Send(new TestPeerQuery("http://bob:8096/", null, saved.Id));

        result.Value.Ok.ShouldBeTrue();
        result.Value.Titles.ShouldBe(2);
        _h.Accounts.SignInCalls.ShouldBeEmpty();
        _h.Accounts.SignOutCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task TestAccountPeer_SavedPeerWithChangedUrl_NeedsPassword()
    {
        var saved = new RemotePeer { Id = Guid.NewGuid(), Name = "bob", BaseUrl = "http://bob:8096", Token = "saved-access", Kind = PeerKind.Account, Username = "carol" };
        _h.Config.Settings.Peers.Add(saved);

        var result = await _h.Send(new TestPeerQuery("http://evil:8096", null, saved.Id));

        result.Value.Ok.ShouldBeFalse();
        result.Value.Error.ShouldStartWith("The address was changed.", Case.Sensitive);
        _h.Peers.Fetches.ShouldBeEmpty();
    }

    [Fact]
    public async Task TestAccountPeer_NoPasswordAndNoSavedPeer_IsAFailedTest()
    {
        var result = await _h.Send(AccountQuery(password: ""));

        result.Value.Ok.ShouldBeFalse();
        result.Value.Error.ShouldBe(PeerErrors.MissingPassword.Description);
        _h.Accounts.SignInCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Success_ReportsServerProtocolAndTitles()
    {
        _h.Peers.Catalogs[Guid.Empty] = _ => new PeerFetchResult(true, false, Catalog(3), null);

        var result = await _h.Send(new TestPeerQuery("http://bob:8096", Token, null));

        result.Value.Ok.ShouldBeTrue();
        result.Value.ServerName.ShouldBe("peer");
        result.Value.ProtocolVersion.ShouldBe(ProtocolInfo.Version);
        result.Value.Titles.ShouldBe(3);
        result.Value.Error.ShouldBeNull();
    }
}
