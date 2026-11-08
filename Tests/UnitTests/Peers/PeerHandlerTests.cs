using JellyShare.Application.Features.Peers;
using JellyShare.Domain.Enums;
using JellyShare.Domain.Models.Admin;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Peers;
using JellyShare.Domain.Models.Requests.Peers;
using JellyShare.Domain.Models.Sync;
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

    // ---- accept list ----

    private void SeedEtag(RemotePeer peer)
        => _h.PeerStates.Save(new Dictionary<Guid, PeerState> { [peer.Id] = new() { Etag = "abc", LastSuccessUtc = DateTimeOffset.UnixEpoch } });

    [Fact]
    public async Task UpdatePeer_AcceptListChange_ClearsEtag()
    {
        var peer = Seed();
        SeedEtag(peer);

        var result = await _h.Send(new UpdatePeerCommand(peer.Id, "alice", "http://alice:8096", null, null, AcceptAllTypes: false, AcceptTypes: [MediaType.Movies]));

        result.IsSuccess.ShouldBeTrue();
        result.Value.AcceptAllTypes.ShouldBeFalse();
        result.Value.AcceptTypes.ShouldBe([MediaType.Movies]);
        var saved = _h.Saver.Saved!.Peers.ShouldHaveSingleItem();
        saved.AcceptAllTypes.ShouldBeFalse();
        saved.AcceptTypes.ShouldBe([MediaType.Movies]);
        var state = _h.PeerStates.Load()[peer.Id];
        state.Etag.ShouldBeNull();
        state.LastSuccessUtc.ShouldBe(DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public async Task UpdatePeer_AcceptListUnchanged_KeepsEtag()
    {
        var peer = Seed();
        SeedEtag(peer);

        await _h.Send(new UpdatePeerCommand(peer.Id, "alice2", "http://alice:8096", null, null, AcceptAllTypes: true));

        _h.PeerStates.Load()[peer.Id].Etag.ShouldBe("abc");
    }

    [Fact]
    public async Task UpdatePeer_KeepsAcceptListWhenOmitted()
    {
        var peer = Seed();
        peer.AcceptAllTypes = false;
        peer.AcceptTypes = [MediaType.Shows];
        SeedEtag(peer);

        var result = await _h.Send(new UpdatePeerCommand(peer.Id, "alice2", "http://alice:8096", null, null));

        result.IsSuccess.ShouldBeTrue();
        var saved = _h.Saver.Saved!.Peers.ShouldHaveSingleItem();
        saved.AcceptAllTypes.ShouldBeFalse();
        saved.AcceptTypes.ShouldBe([MediaType.Shows]);
        _h.PeerStates.Load()[peer.Id].Etag.ShouldBe("abc");
    }

    [Fact]
    public async Task UpdatePeer_OnlyWithoutTypes_FailsValidationAndSavesNothing()
    {
        var peer = Seed();

        var result = await _h.Send(new UpdatePeerCommand(peer.Id, "alice", "http://alice:8096", null, null, AcceptAllTypes: false, AcceptTypes: []));

        result.Error.Type.ShouldBe(ErrorType.Validation);
        _h.Saver.Saves.ShouldBe(0);
    }

    [Fact]
    public async Task UpdatePeer_UnreadablePeerState_StillSaves()
    {
        var peer = Seed();
        Directory.CreateDirectory(_h.Tmp.Sub("data"));
        await File.WriteAllTextAsync(Path.Combine(_h.Tmp.Sub("data"), "peer-state.json"), "{ not json");

        var result = await _h.Send(new UpdatePeerCommand(peer.Id, "alice", "http://alice:8096", null, null, AcceptAllTypes: false, AcceptTypes: [MediaType.Movies]));

        result.IsSuccess.ShouldBeTrue();
        _h.Saver.Saved!.Peers.ShouldHaveSingleItem().AcceptAllTypes.ShouldBeFalse();
    }

    [Fact]
    public async Task UpdateAccountPeer_AcceptListChange_ClearsEtag()
    {
        var peer = SeedAccount();
        SeedEtag(peer);

        var result = await _h.Send(new UpdatePeerCommand(peer.Id, "alice", "http://alice:8096", null, null, AcceptAllTypes: false, AcceptTypes: [MediaType.Shows]));

        result.IsSuccess.ShouldBeTrue();
        _h.Saver.Saved!.Peers.ShouldHaveSingleItem().AcceptTypes.ShouldBe([MediaType.Shows]);
        _h.PeerStates.Load()[peer.Id].Etag.ShouldBeNull();
    }

    [Fact]
    public async Task Add_AcceptList_IsSaved_AndOnlyNeedsAtLeastOneType()
    {
        var ok = await _h.Send(new AddPeerCommand("bob", "http://bob:8096", Token, null, AcceptAllTypes: false, AcceptTypes: [MediaType.Movies]));
        var empty = await _h.Send(new AddPeerCommand("carl", "http://carl:8096", Token, null, AcceptAllTypes: false, AcceptTypes: []));
        var dup = await _h.Send(new AddPeerCommand("dan", "http://dan:8096", Token, null, AcceptAllTypes: false, AcceptTypes: [MediaType.Movies, MediaType.Movies]));

        ok.Value.AcceptTypes.ShouldBe([MediaType.Movies]);
        _h.Saver.Saved!.Peers.Single().AcceptAllTypes.ShouldBeFalse();
        empty.Error.Type.ShouldBe(ErrorType.Validation);
        dup.Error.Type.ShouldBe(ErrorType.Validation);
    }

    [Fact]
    public async Task Add_WithoutAcceptList_AcceptsEverything()
    {
        var result = await _h.Send(new AddPeerCommand("bob", "http://bob:8096", Token, null));

        result.Value.AcceptAllTypes.ShouldBeTrue();
        result.Value.AcceptTypes.ShouldBe(MediaTypes.All);
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

    // ---- account peers ----

    private const string Password = "p@ss-Secret-1";

    private static AddPeerCommand AddAccount(string? password = Password, int? maxStreams = null, string? username = " carol ")
        => new("bob", "http://bob:8096/", null, null, PeerKind.Account, username, password, maxStreams, StreamLimitAction.StopOldest);

    [Fact]
    public async Task AddAccountPeer_SignsInAndStoresTokenNotPassword()
    {
        var result = await _h.Send(AddAccount(maxStreams: 4));

        result.IsSuccess.ShouldBeTrue();
        var saved = _h.Saver.Saved!.Peers.ShouldHaveSingleItem();
        saved.Kind.ShouldBe(PeerKind.Account);
        saved.Username.ShouldBe("carol");
        saved.AccountUserId.ShouldBe("uid-9");
        saved.Token.ShouldBe("access-token-1");
        saved.MaxStreams.ShouldBe(4);
        saved.LimitAction.ShouldBe(StreamLimitAction.StopOldest);
        var call = _h.Accounts.SignInCalls.ShouldHaveSingleItem();
        call.BaseUrl.ShouldBe("http://bob:8096");
        call.Username.ShouldBe("carol");
        call.Password.ShouldBe(Password);
        call.PeerId.ShouldBe(saved.Id);
        System.Text.Json.JsonSerializer.Serialize(_h.Saver.Saved).ShouldNotContain(Password, Case.Sensitive);
        System.Text.Json.JsonSerializer.Serialize(result.Value).ShouldNotContain("access-token-1", Case.Sensitive);
        result.Value.Kind.ShouldBe(PeerKind.Account);
        result.Value.Username.ShouldBe("carol");
    }

    [Fact]
    public async Task AddAccountPeer_Defaults_AreOneStream_AndRefuse()
    {
        await _h.Send(new AddPeerCommand("bob", "http://bob:8096", null, null, PeerKind.Account, "carol", Password));

        var saved = _h.Saver.Saved!.Peers.ShouldHaveSingleItem();
        saved.MaxStreams.ShouldBe(1);
        saved.LimitAction.ShouldBe(StreamLimitAction.Refuse);
    }

    [Fact]
    public async Task AddAccountPeer_SignInFails_SavesNothing_AndLeaksNoSecret()
    {
        _h.Accounts.SignIns = (_, password) => new AccountSignInResult(false, null, null, null, "sign-in failed: wrong username or password " + password);

        var result = await _h.Send(AddAccount());

        result.Error.Type.ShouldBe(ErrorType.Validation);
        result.Error.Description.ShouldStartWith("sign-in failed", Case.Sensitive);
        result.Error.Description.ShouldNotContain(Password, Case.Sensitive);
        _h.Saver.Saves.ShouldBe(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    [InlineData(-1)]
    public async Task AddAccountPeer_MaxStreamsOutOfRange(int maxStreams)
    {
        var result = await _h.Send(AddAccount(maxStreams: maxStreams));

        result.Error.Code.ShouldBe(PeerErrors.InvalidMaxStreams.Code);
        _h.Accounts.SignInCalls.ShouldBeEmpty();
        _h.Saver.Saves.ShouldBe(0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    public async Task AddAccountPeer_MaxStreamsBounds_AreAllowed(int maxStreams)
        => (await _h.Send(AddAccount(maxStreams: maxStreams))).IsSuccess.ShouldBeTrue();

    [Fact]
    public async Task AddAccountPeer_MissingUsernameOrPassword()
    {
        (await _h.Send(AddAccount(username: " "))).Error.Code.ShouldBe(PeerErrors.MissingUsername.Code);
        (await _h.Send(AddAccount(password: ""))).Error.Code.ShouldBe(PeerErrors.MissingPassword.Code);
        _h.Accounts.SignInCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task AddTokenPeer_Unchanged_IgnoresAccountFields()
    {
        var result = await _h.Send(new AddPeerCommand("bob", "http://bob:8096", Token, null, null, "carol", Password, 99, StreamLimitAction.StopOldest));

        result.IsSuccess.ShouldBeTrue();
        var saved = _h.Saver.Saved!.Peers.ShouldHaveSingleItem();
        saved.Kind.ShouldBe(PeerKind.Token);
        saved.Username.ShouldBeEmpty();
        saved.MaxStreams.ShouldBe(1);
        _h.Accounts.SignInCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task UpdateAccountPeer_PasswordReSignsIn()
    {
        var peer = SeedAccount();

        var result = await _h.Send(new UpdatePeerCommand(peer.Id, "alice", "http://alice:8096", null, null, null, null, Password, 5, StreamLimitAction.Refuse));

        result.IsSuccess.ShouldBeTrue();
        var saved = _h.Saver.Saved!.Peers.ShouldHaveSingleItem();
        saved.Token.ShouldBe("access-token-1");
        saved.AccountUserId.ShouldBe("uid-9");
        saved.Username.ShouldBe("carol");
        saved.MaxStreams.ShouldBe(5);
        saved.LimitAction.ShouldBe(StreamLimitAction.Refuse);
        var call = _h.Accounts.SignInCalls.ShouldHaveSingleItem();
        call.Username.ShouldBe("carol");
        call.PeerId.ShouldBe(peer.Id);
        System.Text.Json.JsonSerializer.Serialize(_h.Saver.Saved).ShouldNotContain(Password, Case.Sensitive);
    }

    [Fact]
    public async Task UpdateAccountPeer_PasswordWithNewUsername_UsesTheRequestsUsername()
    {
        var peer = SeedAccount();

        await _h.Send(new UpdatePeerCommand(peer.Id, "alice", "http://alice:8096", null, null, null, " dave ", Password));

        _h.Accounts.SignInCalls.ShouldHaveSingleItem().Username.ShouldBe("dave");
        _h.Saver.Saved!.Peers.ShouldHaveSingleItem().Username.ShouldBe("dave");
    }

    [Fact]
    public async Task UpdateAccountPeer_WithoutPassword_KeepsTokenAndSignsNothing()
    {
        var peer = SeedAccount();

        var result = await _h.Send(new UpdatePeerCommand(peer.Id, "alice2", "http://alice:8096", null, false, null, null, null, 2));

        result.IsSuccess.ShouldBeTrue();
        var saved = _h.Saver.Saved!.Peers.ShouldHaveSingleItem();
        saved.Token.ShouldBe(Token);
        saved.AccountUserId.ShouldBe("uid-1");
        saved.MaxStreams.ShouldBe(2);
        saved.LimitAction.ShouldBe(StreamLimitAction.StopOldest);
        _h.Accounts.SignInCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task UpdateAccountPeer_SignInFails_SavesNothing()
    {
        var peer = SeedAccount();
        _h.Accounts.SignIns = (_, _) => new AccountSignInResult(false, null, null, null, "sign-in failed: wrong username or password");

        var result = await _h.Send(new UpdatePeerCommand(peer.Id, "alice", "http://alice:8096", null, null, null, null, Password));

        result.Error.Description.ShouldStartWith("sign-in failed", Case.Sensitive);
        _h.Saver.Saves.ShouldBe(0);
    }

    [Fact]
    public async Task UpdateAccountPeer_UrlChangeNeedsPassword()
    {
        var peer = SeedAccount();

        var result = await _h.Send(new UpdatePeerCommand(peer.Id, "alice", "http://elsewhere:8096", null, null));

        result.Error.Type.ShouldBe(ErrorType.Validation);
        result.Error.Description.ShouldBe("The address was changed. Enter the password again.");
        _h.Saver.Saves.ShouldBe(0);
    }

    [Fact]
    public async Task UpdateAccountPeer_UrlChangeWithPassword_SignsInAtTheNewUrl()
    {
        var peer = SeedAccount();

        var result = await _h.Send(new UpdatePeerCommand(peer.Id, "alice", "http://elsewhere:8096/", null, null, null, null, Password));

        result.IsSuccess.ShouldBeTrue();
        _h.Accounts.SignInCalls.ShouldHaveSingleItem().BaseUrl.ShouldBe("http://elsewhere:8096");
        _h.Saver.Saved!.Peers.ShouldHaveSingleItem().BaseUrl.ShouldBe("http://elsewhere:8096");
    }

    [Fact]
    public async Task UpdateAccountPeer_NewUsernameWithoutPassword_NeedsPassword()
    {
        var peer = SeedAccount();

        var result = await _h.Send(new UpdatePeerCommand(peer.Id, "alice", "http://alice:8096", null, null, null, "dave"));

        result.Error.Code.ShouldBe(PeerErrors.MissingPassword.Code);
    }

    [Fact]
    public async Task UpdateAccountPeer_MaxStreamsOutOfRange()
    {
        var peer = SeedAccount();

        var result = await _h.Send(new UpdatePeerCommand(peer.Id, "alice", "http://alice:8096", null, null, null, null, null, 21));

        result.Error.Code.ShouldBe(PeerErrors.InvalidMaxStreams.Code);
    }

    [Fact]
    public async Task UpdateAccountPeer_PeerDeletedDuringSignIn_IsNotFound()
    {
        var peer = SeedAccount();
        _h.Accounts.SignIns = (_, _) =>
        {
            _h.Config.Settings.Peers = [];
            return new AccountSignInResult(true, "t", "u", null, null);
        };

        var result = await _h.Send(new UpdatePeerCommand(peer.Id, "alice", "http://alice:8096", null, null, null, null, Password));

        result.Error.Type.ShouldBe(ErrorType.NotFound);
        _h.Saver.Saves.ShouldBe(0);
    }

    [Fact]
    public async Task UpdatePeer_KindChange_IsRefused()
    {
        var account = SeedAccount();
        var token = Seed("tok");

        var toToken = await _h.Send(new UpdatePeerCommand(account.Id, "alice", "http://alice:8096", Token, null, PeerKind.Token));
        var toAccount = await _h.Send(new UpdatePeerCommand(token.Id, "tok", "http://tok:8096", null, null, PeerKind.Account, "carol", Password));

        toToken.Error.Code.ShouldBe(PeerErrors.KindChange.Code);
        toAccount.Error.Code.ShouldBe(PeerErrors.KindChange.Code);
        _h.Saver.Saves.ShouldBe(0);
        _h.Accounts.SignInCalls.ShouldBeEmpty();
    }

    [Fact]
    public void Commands_NeverPrintThePassword()
    {
        new AddPeerCommand("bob", "http://bob", null, true, PeerKind.Account, "carol", Password).ToString().ShouldNotContain(Password, Case.Sensitive);
        new UpdatePeerCommand(Guid.Empty, "bob", "http://bob", null, true, PeerKind.Account, "carol", Password).ToString().ShouldNotContain(Password, Case.Sensitive);
        new TestPeerQuery("http://bob", null, null, PeerKind.Account, "carol", Password).ToString().ShouldNotContain(Password, Case.Sensitive);
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
    public async Task Delete_AccountPeer_SignsOutOnTheFriendsServer()
    {
        var peer = SeedAccount();

        var result = await _h.Send(new DeletePeerCommand(peer.Id));

        result.IsSuccess.ShouldBeTrue();
        _h.Accounts.SignOutCalls.ShouldHaveSingleItem().ShouldBe((peer.BaseUrl, Token, peer.Id));
    }

    [Fact]
    public async Task Delete_TokenPeer_DoesNotSignOut()
    {
        var peer = Seed();

        await _h.Send(new DeletePeerCommand(peer.Id));

        _h.Accounts.SignOutCalls.ShouldBeEmpty();
    }

    [Fact]
    public void Requests_NeverPrintThePasswordOrToken()
    {
        var add = new AddPeerRequest("bob", "http://bob", Token, true, PeerKind.Account, "carol", Password).ToString();
        var update = new UpdatePeerRequest("bob", "http://bob", Token, true, PeerKind.Account, "carol", Password).ToString();
        var test = new TestPeerRequest("http://bob", Token, null, PeerKind.Account, "carol", Password).ToString();

        foreach (var text in new[] { add, update, test })
        {
            text.ShouldNotContain(Password, Case.Sensitive);
            text.ShouldNotContain(Token, Case.Sensitive);
        }
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
