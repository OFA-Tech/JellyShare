using JellyShare.Domain.Enums;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Requests.Peers;
using JellyShare.UnitTests.Fakes;

namespace JellyShare.UnitTests.Peers;

public sealed class PeerHandlerTests : IDisposable
{
    private const string Token = "js1_PEER-secret-token";
    private const string InvalidUrl = "The URL is not valid. Use the full address of the other server, for example http://host:8096 or https://host/jellyfin.";

    private readonly AdminHarness _h = new();

    public void Dispose() => _h.Dispose();

    private RemotePeer Seed(string name = "alice", int priority = 0)
    {
        var peer = new RemotePeer { Id = Guid.NewGuid(), Name = name, BaseUrl = "http://" + name + ":8096", Token = Token, Enabled = true, Priority = priority };
        _h.Config.Settings.Peers.Add(peer);
        return peer;
    }

    // ---- add ----

    [Fact]
    public async Task Add_NormalizesAndSaves_WithTheNextPriority()
    {
        Seed(priority: 4);

        var result = await _h.Send(new AddPeerCommand(" bob ", "http://bob:8096/", " " + Token + " ", null));

        result.IsSuccess.ShouldBeTrue();
        result.Value.Name.ShouldBe("bob");
        result.Value.BaseUrl.ShouldBe("http://bob:8096");
        result.Value.Priority.ShouldBe(5);
        result.Value.HasToken.ShouldBeTrue();
        _h.Saver.Saved!.Peers.Single(p => p.Name == "bob").Token.ShouldBe(Token);
    }

    [Theory]
    [InlineData("", "http://bob:8096", Token, "Enter a name for the peer.")]
    [InlineData("bob", "ftp://bob", Token, InvalidUrl)]
    [InlineData("bob", "http://user:pw@bob:8096", Token, InvalidUrl)]
    [InlineData("bob", "http://bob:8096", "  ", "Enter the token from the invitation.")]
    [InlineData("bob", "http://bob:8096", "a\nb", "The token contains line breaks or other control characters. Paste only the token.")]
    public async Task Add_InvalidFields_FailValidation_AndSaveNothing(string name, string url, string token, string message)
    {
        var result = await _h.Send(new AddPeerCommand(name, url, token, true));

        result.Error.Type.ShouldBe(ErrorType.Validation);
        result.Error.Description.ShouldBe(message);
        _h.Saver.Saves.ShouldBe(0);
    }

    [Fact]
    public async Task Add_TooLongToken_FailsValidation()
    {
        var result = await _h.Send(new AddPeerCommand("bob", "http://bob:8096", new string('x', 513), true));

        result.Error.Description.ShouldBe("The token is too long (at most 512 characters). Check that you pasted only the token.");
    }

    [Fact]
    public void Commands_NeverPrintTheToken()
    {
        (new AddPeerCommand("bob", "http://bob", Token, true).ToString()).ShouldNotContain(Token, Case.Sensitive);
        (new UpdatePeerCommand(Guid.Empty, "bob", "http://bob", Token, true).ToString()).ShouldNotContain(Token, Case.Sensitive);
        (new TestPeerQuery("http://bob", Token, null).ToString()).ShouldNotContain(Token, Case.Sensitive);
    }

    // ---- update ----

    [Fact]
    public async Task Update_EmptyToken_KeepsTheStoredOne_AndThePriority()
    {
        var peer = Seed(priority: 3);

        var result = await _h.Send(new UpdatePeerCommand(peer.Id, "alice2", "https://alice/jf", "", false));

        result.IsSuccess.ShouldBeTrue();
        var saved = _h.Saver.Saved!.Peers.ShouldHaveSingleItem();
        saved.Token.ShouldBe(Token);
        saved.Priority.ShouldBe(3);
        saved.BaseUrl.ShouldBe("https://alice/jf");
        saved.Enabled.ShouldBeFalse();
    }

    [Fact]
    public async Task Update_NewToken_ReplacesIt()
    {
        var peer = Seed();

        await _h.Send(new UpdatePeerCommand(peer.Id, "alice", "http://alice:8096", "new-token", null));

        _h.Saver.Saved!.Peers.ShouldHaveSingleItem().Token.ShouldBe("new-token");
    }

    private RemotePeer SeedAccount()
    {
        var peer = Seed();
        peer.Kind = PeerKind.Account;
        peer.Username = "carol";
        peer.AccountUserId = "uid-1";
        peer.MaxStreams = 3;
        peer.LimitAction = StreamLimitAction.StopOldest;
        return peer;
    }

    private static void ShouldBeAccountPeer(RemotePeer saved)
    {
        saved.Kind.ShouldBe(PeerKind.Account);
        saved.Username.ShouldBe("carol");
        saved.AccountUserId.ShouldBe("uid-1");
        saved.MaxStreams.ShouldBe(3);
        saved.LimitAction.ShouldBe(StreamLimitAction.StopOldest);
    }

    [Fact]
    public async Task UpdatePeer_KeepsAccountFields()
    {
        var peer = SeedAccount();

        await _h.Send(new UpdatePeerCommand(peer.Id, "alice2", "http://alice:8096", null, null));

        ShouldBeAccountPeer(_h.Saver.Saved!.Peers.ShouldHaveSingleItem());
    }

    [Fact]
    public async Task ReorderPeers_KeepsAccountFields()
    {
        var peer = SeedAccount();

        await _h.Send(new ReorderPeersCommand([peer.Id]));

        ShouldBeAccountPeer(_h.Saver.Saved!.Peers.ShouldHaveSingleItem());
    }

    [Fact]
    public async Task Update_UnknownPeer_IsNotFound_EvenWithInvalidFields()
    {
        var result = await _h.Send(new UpdatePeerCommand(Guid.NewGuid(), "", "bad", null, null));

        result.Error.Type.ShouldBe(ErrorType.NotFound);
        result.Error.Description.ShouldBe("Peer not found.");
    }

    [Theory]
    [InlineData("", "http://alice:8096", null, "Enter a name for the peer.")]
    [InlineData("alice", "nope", null, InvalidUrl)]
    [InlineData("alice", "http://alice:8096", "a\tb", "The token contains line breaks or other control characters. Paste only the token.")]
    public async Task Update_InvalidFields_FailValidation(string name, string url, string? token, string message)
    {
        var peer = Seed();

        var result = await _h.Send(new UpdatePeerCommand(peer.Id, name, url, token, null));

        result.Error.Type.ShouldBe(ErrorType.Validation);
        result.Error.Description.ShouldBe(message);
        _h.Saver.Saves.ShouldBe(0);
    }

    // ---- delete ----

    [Fact]
    public async Task Delete_RemovesThePeer_AndQueuesASync()
    {
        var peer = Seed();

        var result = await _h.Send(new DeletePeerCommand(peer.Id));

        result.IsSuccess.ShouldBeTrue();
        _h.Saver.Saved!.Peers.ShouldBeEmpty();
        _h.Sync.LastQueueIfRunning.ShouldBe(true);
    }

    [Fact]
    public async Task Delete_UnknownPeer_IsNotFound_AndStartsNoSync()
    {
        var result = await _h.Send(new DeletePeerCommand(Guid.NewGuid()));

        result.Error.Type.ShouldBe(ErrorType.NotFound);
        result.Error.Description.ShouldBe("Peer not found.");
        _h.Sync.LastQueueIfRunning.ShouldBeNull();
    }

    // ---- reorder ----

    [Fact]
    public async Task Reorder_ListedFirst_UnlistedKeepTheirOrderAfter_UnknownIgnored()
    {
        var a = Seed("a", 0);
        var b = Seed("b", 1);
        var c = Seed("c", 2);

        var result = await _h.Send(new ReorderPeersCommand([c.Id, Guid.NewGuid(), c.Id, a.Id]));

        result.IsSuccess.ShouldBeTrue();
        var priorities = _h.Saver.Saved!.Peers.ToDictionary(p => p.Name, p => p.Priority);
        priorities["c"].ShouldBe(0);
        priorities["a"].ShouldBe(1);
        priorities["b"].ShouldBe(2);
        _h.Saver.Saved.Peers.Single(p => p.Name == "b").ShouldNotBeSameAs(b);
    }
}
