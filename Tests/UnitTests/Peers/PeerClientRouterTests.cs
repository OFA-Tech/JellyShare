using JellyShare.Data.Peers.Clients;
using JellyShare.Domain.Enums;
using JellyShare.Domain.Models.Configuration;
using JellyShare.UnitTests.Fakes;

namespace JellyShare.UnitTests.Peers;

public sealed class PeerClientRouterTests
{
    [Fact]
    public async Task Router_DispatchesByKind()
    {
        var tokens = new FakePeerClient();
        var accounts = new FakePeerClient();
        var router = new PeerClientRouter(tokens, accounts);
        var tokenPeer = new RemotePeer { Id = Guid.NewGuid(), Kind = PeerKind.Token };
        var accountPeer = new RemotePeer { Id = Guid.NewGuid(), Kind = PeerKind.Account };
        var item = TestItems.Movie("n");

        await router.FetchCatalogAsync(tokenPeer, null, CancellationToken.None);
        await router.FetchCatalogAsync(accountPeer, null, CancellationToken.None);
        await router.GetImageAsync(tokenPeer, "i", "Primary", CancellationToken.None);
        await router.GetImageAsync(accountPeer, "i", "Primary", CancellationToken.None);
        using var tokenStream = await router.OpenStreamAsync(tokenPeer, item, null, CancellationToken.None);
        using var accountStream = await router.OpenStreamAsync(accountPeer, item, null, CancellationToken.None);
        await router.PingAsync("https://h", CancellationToken.None);

        tokens.Fetches.ShouldHaveSingleItem().Peer.ShouldBe(tokenPeer.Id);
        accounts.Fetches.ShouldHaveSingleItem().Peer.ShouldBe(accountPeer.Id);
        tokens.ImageRequests.ShouldHaveSingleItem().Peer.ShouldBe(tokenPeer.Id);
        accounts.ImageRequests.ShouldHaveSingleItem().Peer.ShouldBe(accountPeer.Id);
        tokens.StreamRequests.ShouldBe([tokenPeer.Id]);
        accounts.StreamRequests.ShouldBe([accountPeer.Id]);
        tokens.PingedUrls.ShouldBe(["https://h"]);
        accounts.PingedUrls.ShouldBeEmpty();
    }
}
