using JellyShare.Application.Features.Admin;
using JellyShare.Domain.Enums;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Sync;

namespace JellyShare.UnitTests.Peers;

public sealed class RemotePeerTests
{
    [Fact]
    public void RemotePeer_Defaults()
    {
        var peer = new RemotePeer();

        peer.Kind.ShouldBe(PeerKind.Token);
        peer.MaxStreams.ShouldBe(1);
        peer.LimitAction.ShouldBe(StreamLimitAction.Refuse);
        peer.Username.ShouldBe(string.Empty);
        peer.AccountUserId.ShouldBe(string.Empty);
    }

    [Fact]
    public void RemotePeer_Copy_CopiesEveryProperty()
    {
        var peer = new RemotePeer
        {
            Id = Guid.NewGuid(),
            Name = "n",
            BaseUrl = "http://x",
            Token = "t",
            Enabled = false,
            Priority = 7,
            Kind = PeerKind.Account,
            Username = "u",
            AccountUserId = "uid",
            MaxStreams = 3,
            LimitAction = StreamLimitAction.StopOldest,
        };

        var copy = peer.Copy();

        copy.ShouldNotBeSameAs(peer);
        foreach (var property in typeof(RemotePeer).GetProperties())
        {
            property.GetValue(copy).ShouldBe(property.GetValue(peer), property.Name);
            // every property differs from a fresh peer, so a field Copy() forgets would show up here
            property.GetValue(peer).ShouldNotBe(property.GetValue(new RemotePeer()), property.Name);
        }
    }

    [Fact]
    public void ToView_AccountPeer()
    {
        var peer = new RemotePeer { Id = Guid.NewGuid(), Kind = PeerKind.Account, Username = "u", Token = "secret", MaxStreams = 2, LimitAction = StreamLimitAction.StopOldest };
        var states = new Dictionary<Guid, PeerState> { [peer.Id] = new PeerState { LastError = "sign-in failed: HTTP 401" } };

        var view = AdminRules.ToView(peer, states);

        view.SignInFailed.ShouldBeTrue();
        view.Kind.ShouldBe(PeerKind.Account);
        view.Username.ShouldBe("u");
        view.MaxStreams.ShouldBe(2);
        view.LimitAction.ShouldBe(StreamLimitAction.StopOldest);
        view.HasToken.ShouldBeTrue();
        view.ToString().ShouldNotContain("secret", Case.Sensitive);
        AdminRules.ToView(peer, new Dictionary<Guid, PeerState>()).SignInFailed.ShouldBeFalse();
    }
}
