using JellyShare.Domain.Enums;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Requests.Peers;
using JellyShare.UnitTests.Fakes;
using Xunit;

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

        Assert.True(result.IsSuccess);
        Assert.Equal("bob", result.Value.Name);
        Assert.Equal("http://bob:8096", result.Value.BaseUrl);
        Assert.Equal(5, result.Value.Priority);
        Assert.True(result.Value.HasToken);
        Assert.Equal(Token, _h.Saver.Saved!.Peers.Single(p => p.Name == "bob").Token);
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

        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal(message, result.Error.Description);
        Assert.Equal(0, _h.Saver.Saves);
    }

    [Fact]
    public async Task Add_TooLongToken_FailsValidation()
    {
        var result = await _h.Send(new AddPeerCommand("bob", "http://bob:8096", new string('x', 513), true));

        Assert.Equal("The token is too long (at most 512 characters). Check that you pasted only the token.", result.Error.Description);
    }

    [Fact]
    public void Commands_NeverPrintTheToken()
    {
        Assert.DoesNotContain(Token, new AddPeerCommand("bob", "http://bob", Token, true).ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Token, new UpdatePeerCommand(Guid.Empty, "bob", "http://bob", Token, true).ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Token, new TestPeerQuery("http://bob", Token, null).ToString(), StringComparison.Ordinal);
    }

    // ---- update ----

    [Fact]
    public async Task Update_EmptyToken_KeepsTheStoredOne_AndThePriority()
    {
        var peer = Seed(priority: 3);

        var result = await _h.Send(new UpdatePeerCommand(peer.Id, "alice2", "https://alice/jf", "", false));

        Assert.True(result.IsSuccess);
        var saved = Assert.Single(_h.Saver.Saved!.Peers);
        Assert.Equal(Token, saved.Token);
        Assert.Equal(3, saved.Priority);
        Assert.Equal("https://alice/jf", saved.BaseUrl);
        Assert.False(saved.Enabled);
    }

    [Fact]
    public async Task Update_NewToken_ReplacesIt()
    {
        var peer = Seed();

        await _h.Send(new UpdatePeerCommand(peer.Id, "alice", "http://alice:8096", "new-token", null));

        Assert.Equal("new-token", Assert.Single(_h.Saver.Saved!.Peers).Token);
    }

    [Fact]
    public async Task Update_UnknownPeer_IsNotFound_EvenWithInvalidFields()
    {
        var result = await _h.Send(new UpdatePeerCommand(Guid.NewGuid(), "", "bad", null, null));

        Assert.Equal(ErrorType.NotFound, result.Error.Type);
        Assert.Equal("Peer not found.", result.Error.Description);
    }

    [Theory]
    [InlineData("", "http://alice:8096", null, "Enter a name for the peer.")]
    [InlineData("alice", "nope", null, InvalidUrl)]
    [InlineData("alice", "http://alice:8096", "a\tb", "The token contains line breaks or other control characters. Paste only the token.")]
    public async Task Update_InvalidFields_FailValidation(string name, string url, string? token, string message)
    {
        var peer = Seed();

        var result = await _h.Send(new UpdatePeerCommand(peer.Id, name, url, token, null));

        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal(message, result.Error.Description);
        Assert.Equal(0, _h.Saver.Saves);
    }

    // ---- delete ----

    [Fact]
    public async Task Delete_RemovesThePeer_AndQueuesASync()
    {
        var peer = Seed();

        var result = await _h.Send(new DeletePeerCommand(peer.Id));

        Assert.True(result.IsSuccess);
        Assert.Empty(_h.Saver.Saved!.Peers);
        Assert.True(_h.Sync.LastQueueIfRunning);
    }

    [Fact]
    public async Task Delete_UnknownPeer_IsNotFound_AndStartsNoSync()
    {
        var result = await _h.Send(new DeletePeerCommand(Guid.NewGuid()));

        Assert.Equal(ErrorType.NotFound, result.Error.Type);
        Assert.Equal("Peer not found.", result.Error.Description);
        Assert.Null(_h.Sync.LastQueueIfRunning);
    }

    // ---- reorder ----

    [Fact]
    public async Task Reorder_ListedFirst_UnlistedKeepTheirOrderAfter_UnknownIgnored()
    {
        var a = Seed("a", 0);
        var b = Seed("b", 1);
        var c = Seed("c", 2);

        var result = await _h.Send(new ReorderPeersCommand([c.Id, Guid.NewGuid(), c.Id, a.Id]));

        Assert.True(result.IsSuccess);
        var priorities = _h.Saver.Saved!.Peers.ToDictionary(p => p.Name, p => p.Priority);
        Assert.Equal(0, priorities["c"]);
        Assert.Equal(1, priorities["a"]);
        Assert.Equal(2, priorities["b"]);
        Assert.NotSame(b, _h.Saver.Saved.Peers.Single(p => p.Name == "b"));
    }
}
