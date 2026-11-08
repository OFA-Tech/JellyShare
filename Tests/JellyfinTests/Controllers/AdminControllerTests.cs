using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JellyShare.Application.Features.Catalog;
using JellyShare.Application.Setup;
using JellyShare.Application.Features.Streaming;
using JellyShare.Data.Files.Placeholders;
using JellyShare.Data.Files.Stores;
using JellyShare.Domain.Exceptions;
using JellyShare.Domain.Interfaces.Catalog;
using JellyShare.Domain.Models.Placeholders;
using JellyShare.Domain.Enums;
using JellyShare.Domain.Interfaces.Configuration;
using JellyShare.Domain.Interfaces.Library;
using JellyShare.Domain.Interfaces.Messaging;
using JellyShare.Domain.Interfaces.Peers;
using JellyShare.Domain.Interfaces.Placeholders;
using JellyShare.Domain.Interfaces.Platform;
using JellyShare.Domain.Interfaces.Sync;
using JellyShare.Domain.Models.Admin;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Peers;
using JellyShare.Domain.Models.Protocol;
using JellyShare.Domain.Models.Sync;
using JellyShare.Jellyfin.Controllers.V1;
using JellyShare.UnitTests.Fakes;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JellyShare.JellyfinTests.Controllers;

public sealed class AdminControllerTests : IDisposable
{
    private const string Token = "js1_PEER-secret-token";

    private readonly TempDir _tmp = new();
    private readonly ConsumerConfig _config = new();
    private readonly FakeSaver _saver;
    private readonly ShareTokenService _tokens = new();
    private readonly StreamLimiter _limiter = new(TimeProvider.System);
    private readonly FakePeerClient _peers = new();
    private readonly FakeSync _sync = new();
    private readonly FakeLibraries _libs = new();
    private readonly ConsumerIndexFiles _index;
    private readonly ServiceProvider _services;
    private readonly AdminController _c;
    private readonly Microsoft.AspNetCore.Http.DefaultHttpContext _http = new();

    public AdminControllerTests()
    {
        _saver = new FakeSaver(_config);
        _config.Settings.MoviesPlaceholderPath = _tmp.Sub("ph", "Movies");
        _config.Settings.ShowsPlaceholderPath = _tmp.Sub("ph", "Shows");
        _index = new ConsumerIndexFiles(() => _tmp.Sub("data"));
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<ISettingsProvider>(_config);
        services.AddSingleton<ISettingsStore>(_saver);
        services.AddSingleton<IShareTokenService>(_tokens);
        services.AddSingleton(_limiter);
        services.AddSingleton<IPeerClient>(_peers);
        services.AddSingleton<IAccountSignIn>(new FakeAccountSignIn());
        services.AddSingleton<ISyncRunner>(_sync);
        services.AddSingleton<IConsumerIndex>(_index);
        services.AddSingleton<JellyShare.Domain.Interfaces.Storage.IPeerStateStore>(PeerStateStore.InDataFolder(() => _tmp.Sub("data")));
        services.AddSingleton<IAdminLibraries>(_libs);
        services.AddSingleton<IPlaceholderFiles>(new PlaceholderWriter());
        services.AddSingleton<IServerIdentity>(new FakeIdentity());
        services.AddDispatcherSetup();
        _services = services.BuildServiceProvider();
        _c = new AdminController(_services.GetRequiredService<ISender>())
        {
            ControllerContext = new ControllerContext { HttpContext = _http },
        };
    }

    [Fact]
    public void CreateShare_ResponseIsNotCacheable()
    {
        CreateShare();

        _http.Response.Headers.CacheControl.ToString().ShouldBe("no-store");
    }

    public void Dispose()
    {
        _services.Dispose();
        _tmp.Dispose();
    }

    // The admin use cases complete synchronously (only TestPeer and Attach do I/O, and those tests await).
    private static IActionResult Run(Task<IActionResult> action) => action.GetAwaiter().GetResult();

    private static T Value<T>(IActionResult result)
        => result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<T>();

    private static string ErrorOf(IActionResult result)
    {
        var value = result switch
        {
            BadRequestObjectResult b => b.Value,
            NotFoundObjectResult n => n.Value,
            ObjectResult o => o.Value,
            _ => null,
        };
        return value.ShouldBeOfType<ErrorResponse>().Error;
    }

    private CreatedShareResponse CreateShare(string name = "bob", int streams = 2, int? bitrate = null, params Guid[] libs)
        => Value<CreatedShareResponse>(Run(_c.CreateShare(new CreateShareRequest(name, libs.Length == 0 ? [FakeLibraries.MoviesId] : libs.ToList(), streams, bitrate), CancellationToken.None)));

    private static string Sha256Hex(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private StatusResponse Status() => Value<StatusResponse>(Run(_c.Status(CancellationToken.None)));

    private RemotePeer SeedPeer(string name = "alice", int priority = 0, bool enabled = true)
    {
        var peer = new RemotePeer { Id = Guid.NewGuid(), Name = name, BaseUrl = "http://" + name + ":8096", Token = Token, Enabled = enabled, Priority = priority };
        _config.Settings.Peers.Add(peer);
        return peer;
    }

    // ---- authorization ----

    [Fact]
    public void Admin_EveryRouteRequiresElevation_NoneAnonymous()
    {
        var type = typeof(AdminController);
        var policy = type.GetCustomAttributes<AuthorizeAttribute>().SingleOrDefault();
        policy.ShouldNotBeNull();
        policy.Policy.ShouldBe(Policies.RequiresElevation);
        type.GetCustomAttributes<AllowAnonymousAttribute>().ShouldBeEmpty();

        var actions = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any())
            .ToList();
        actions.Count.ShouldBeGreaterThanOrEqualTo(11);
        foreach (var action in actions)
        {
            action.GetCustomAttributes<AllowAnonymousAttribute>().ShouldBeEmpty();
        }

        foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            m.GetCustomAttributes<HttpMethodAttribute>().Any().ShouldBeTrue(m.Name + " is public but has no route");
        }
    }

    // ---- shares ----

    [Fact]
    public void CreateShare_ReturnsTokenOnce_StoresOnlyHash()
    {
        var created = CreateShare("bob", 3, 8_000_000);

        created.Token.ShouldStartWith("js1_", Case.Sensitive);
        var share = _config.Current.Shares.ShouldHaveSingleItem();
        share.Id.ShouldBe(created.Id);
        share.Name.ShouldBe("bob");
        share.TokenHash.ShouldBe(Sha256Hex(created.Token));
        share.TokenHash.ShouldNotBe(created.Token);
        share.MaxStreams.ShouldBe(3);
        share.MaxBitrate.ShouldBe(8_000_000);
        share.Enabled.ShouldBeTrue();
        share.UseDefaultTypes.ShouldBe(true);
        _tokens.Verify(created.Token, _config.Current.Shares).ShouldNotBeNull();
        (_saver.Saves >= 1).ShouldBeTrue();
        _saver.Saved!.Shares.ShouldHaveSingleItem().Id.ShouldBe(created.Id);
    }

    [Fact]
    public void CreateShare_TwoCalls_GiveDifferentTokens()
    {
        CreateShare("b").Token.ShouldNotBe(CreateShare("a").Token);
    }

    [Fact]
    public void Status_NeverContainsTokenOrHash()
    {
        var created = CreateShare();
        var json = JsonSerializer.Serialize(Status());

        json.ShouldNotContain(created.Token, Case.Sensitive);
        json.ShouldNotContain(Sha256Hex(created.Token), Case.Sensitive);
        json.ShouldNotContain("TokenHash", Case.Insensitive);
    }

    [Theory]
    [InlineData("", 2, null)]
    [InlineData("   ", 2, null)]
    [InlineData("bob", 0, null)]
    [InlineData("bob", 101, null)]
    [InlineData("bob", 2, 0)]
    [InlineData("bob", 2, -5)]
    public void CreateShare_InvalidInput_IsBadRequest_AndStoresNothing(string name, int streams, int? bitrate)
    {
        var result = Run(_c.CreateShare(new CreateShareRequest(name, [FakeLibraries.MoviesId], streams, bitrate), CancellationToken.None));

        result.ShouldBeOfType<BadRequestObjectResult>();
        _config.Current.Shares.ShouldBeEmpty();
        _saver.Saves.ShouldBe(0);
    }

    [Fact]
    public void CreateShare_CustomWithoutTypes_IsBadRequest()
    {
        var result = Run(_c.CreateShare(new CreateShareRequest("bob", null, 2, null, false, []), CancellationToken.None));

        result.ShouldBeOfType<BadRequestObjectResult>();
        _config.Current.Shares.ShouldBeEmpty();
    }

    [Fact]
    public void DefaultSharing_IsInStatus_AndCanBeChanged()
    {
        Status().DefaultSharing.All.ShouldBeTrue();
        Status().SupportedTypes.ShouldBe([MediaType.Movies, MediaType.Shows]);

        var view = Value<DefaultSharingView>(Run(_c.UpdateDefaultSharing(new DefaultSharingRequest(false, [MediaType.Music, MediaType.Movies]), CancellationToken.None)));

        view.All.ShouldBeFalse();
        view.Types.ShouldBe([MediaType.Movies, MediaType.Music]);
        Status().DefaultSharing.Types.ShouldBe(view.Types);
        Status().DefaultSharing.All.ShouldBeFalse();
        Run(_c.UpdateDefaultSharing(null!, CancellationToken.None)).ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void CreateShare_NullBody_IsBadRequest()
    {
        Run(_c.CreateShare(null!, CancellationToken.None)).ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void UpdateShare_ChangesNameTypesLimitsEnabled_KeepsToken()
    {
        var created = CreateShare();
        var hashBefore = _config.Current.Shares[0].TokenHash;

        var result = Run(_c.UpdateShare(created.Id, new UpdateShareRequest("bobby", null, 5, 4_000_000, false, false, [MediaType.Shows]), CancellationToken.None));

        var view = Value<ShareView>(result);
        var share = _config.Current.Shares.Single();
        share.Name.ShouldBe("bobby");
        share.UseDefaultTypes.ShouldBe(false);
        share.Types.ShouldBe([MediaType.Shows]);
        view.UseDefaultTypes.ShouldBeFalse();
        view.Types.ShouldBe([MediaType.Shows]);
        share.MaxStreams.ShouldBe(5);
        share.MaxBitrate.ShouldBe(4_000_000);
        share.Enabled.ShouldBeFalse();
        share.TokenHash.ShouldBe(hashBefore);
        _saver.Saved!.Shares.ShouldHaveSingleItem().Name.ShouldBe("bobby");
        view.Id.ShouldBe(created.Id);
        view.Enabled.ShouldBeFalse();
        _tokens.Verify(created.Token, _config.Current.Shares).ShouldBeNull();

        Run(_c.UpdateShare(created.Id, new UpdateShareRequest("bobby", [FakeLibraries.MoviesId], 5, null, true), CancellationToken.None));
        _tokens.Verify(created.Token, _config.Current.Shares).ShouldNotBeNull();
        _config.Current.Shares.Single().MaxBitrate.ShouldBeNull();
    }

    [Fact]
    public void UpdateShare_Unknown_IsNotFound()
    {
        var result = Run(_c.UpdateShare(Guid.NewGuid(), new UpdateShareRequest("x", [], 2, null, true), CancellationToken.None));
        result.ShouldBeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public void UpdateShare_Invalid_ChangesNothing()
    {
        var created = CreateShare();
        var saves = _saver.Saves;

        var result = Run(_c.UpdateShare(created.Id, new UpdateShareRequest("", [FakeLibraries.MoviesId], 2, null, true), CancellationToken.None));

        result.ShouldBeOfType<BadRequestObjectResult>();
        _config.Current.Shares.Single().Name.ShouldBe("bob");
        _saver.Saves.ShouldBe(saves);
    }

    [Fact]
    public void RevokeShare_RemovesItAndTheTokenStopsWorking()
    {
        var created = CreateShare();
        var other = CreateShare("carol");
        _tokens.Verify(created.Token, _config.Current.Shares).ShouldNotBeNull();

        var result = Run(_c.DeleteShare(created.Id, CancellationToken.None));

        result.ShouldBeOfType<NoContentResult>();
        _tokens.Verify(created.Token, _config.Current.Shares).ShouldBeNull();
        _tokens.Verify(other.Token, _config.Current.Shares).ShouldNotBeNull();
        _config.Current.Shares.ShouldHaveSingleItem().Id.ShouldBe(other.Id);
        _saver.Saved!.Shares.ShouldHaveSingleItem().Id.ShouldBe(other.Id);
    }

    [Fact]
    public void RevokeShare_Unknown_IsNotFound()
    {
        Run(_c.DeleteShare(Guid.NewGuid(), CancellationToken.None)).ShouldBeOfType<NotFoundObjectResult>();
    }

    // ---- peers ----

    [Fact]
    public void AddPeer_NormalizesUrl_AppendsPriority_NeverReturnsToken()
    {
        SeedPeer("alice", 4);

        var result = Run(_c.AddPeer(new AddPeerRequest("  carol ", " http://carol:8096/jellyfin/ ", Token, null), CancellationToken.None));

        var view = Value<PeerView>(result);
        view.Name.ShouldBe("carol");
        view.BaseUrl.ShouldBe("http://carol:8096/jellyfin");
        view.HasToken.ShouldBeTrue();
        view.Enabled.ShouldBeTrue();
        view.Priority.ShouldBe(5);
        JsonSerializer.Serialize(view).ShouldNotContain(Token, Case.Sensitive);
        var stored = _config.Current.Peers.Single(p => p.Name == "carol");
        stored.Token.ShouldBe(Token);
        stored.Id.ShouldNotBe(Guid.Empty);
        stored.Id.ShouldBe(view.Id);
        (_saver.Saves >= 1).ShouldBeTrue();
        _saver.Saved!.Peers.ShouldContain(p => p.Id == view.Id);
    }

    [Theory]
    [InlineData("", "http://x:8096", Token)]
    [InlineData("n", "not a url", Token)]
    [InlineData("n", "ftp://x/", Token)]
    [InlineData("n", "http://x:8096", "")]
    [InlineData("n", "http://x:8096", "   ")]
    public void AddPeer_Invalid_IsBadRequest(string name, string url, string token)
    {
        var result = Run(_c.AddPeer(new AddPeerRequest(name, url, token, true), CancellationToken.None));

        result.ShouldBeOfType<BadRequestObjectResult>();
        _config.Current.Peers.ShouldBeEmpty();
    }

    [Fact]
    public void UpdatePeer_EmptyToken_KeepsStoredToken()
    {
        var peer = SeedPeer();

        var result = Run(_c.UpdatePeer(peer.Id, new UpdatePeerRequest("alice2", "http://alice2:8096/", "", false), CancellationToken.None));

        var view = Value<PeerView>(result);
        var stored = _config.Current.Peers.Single();
        stored.Name.ShouldBe("alice2");
        stored.BaseUrl.ShouldBe("http://alice2:8096");
        stored.Enabled.ShouldBeFalse();
        stored.Token.ShouldBe(Token);
        stored.Priority.ShouldBe(peer.Priority);
        view.Enabled.ShouldBeFalse();
    }

    [Fact]
    public void UpdatePeer_NewToken_ReplacesIt()
    {
        var peer = SeedPeer();

        Run(_c.UpdatePeer(peer.Id, new UpdatePeerRequest("alice", peer.BaseUrl, "js1_new", true), CancellationToken.None));

        _config.Current.Peers.Single().Token.ShouldBe("js1_new");
        _saver.Saved!.Peers.ShouldHaveSingleItem().Token.ShouldBe("js1_new");
    }

    [Fact]
    public void UpdatePeer_IsCopyOnWrite_ARunningSyncKeepsASelfConsistentPeer()
    {
        var peer = SeedPeer();
        var listBefore = _config.Current.Peers;

        Run(_c.UpdatePeer(peer.Id, new UpdatePeerRequest("renamed", "http://other:8096", "js1_new", false), CancellationToken.None));

        peer.Name.ShouldBe("alice");
        peer.BaseUrl.ShouldBe("http://alice:8096");
        peer.Token.ShouldBe(Token);
        peer.Enabled.ShouldBeTrue();
        _config.Current.Peers.ShouldNotBeSameAs(listBefore);
        _config.Current.Peers.Single().ShouldNotBeSameAs(peer);
        _saver.Saved!.Peers.ShouldHaveSingleItem().Name.ShouldBe("renamed");
    }

    [Fact]
    public void UpdateShare_IsCopyOnWrite()
    {
        var created = CreateShare();
        var before = _config.Current.Shares.Single();

        Run(_c.UpdateShare(created.Id, new UpdateShareRequest("renamed", [FakeLibraries.ShowsId], 7, 1_000_000, false), CancellationToken.None));

        before.Name.ShouldBe("bob");
        before.MaxStreams.ShouldBe(2);
        before.Enabled.ShouldBeTrue();
        _config.Current.Shares.Single().ShouldNotBeSameAs(before);
        _config.Current.Shares.Single().Name.ShouldBe("renamed");
        _saver.Saved!.Shares.ShouldHaveSingleItem().Name.ShouldBe("renamed");
    }

    [Fact]
    public void ReorderPeers_IsCopyOnWrite()
    {
        var a = SeedPeer("a", 0);
        var b = SeedPeer("b", 1);

        Run(_c.ReorderPeers(new ReorderPeersRequest([b.Id, a.Id]), CancellationToken.None));

        a.Priority.ShouldBe(0);
        b.Priority.ShouldBe(1);
        Status().Peers.Select(p => p.Name).ShouldBe(["b", "a"]);
    }

    [Theory]
    [InlineData("js1_bad\ntoken")]
    [InlineData("js1_bad\u0000token")]
    public void Peer_TokenWithControlCharacters_IsBadRequest(string token)
    {
        Run(_c.AddPeer(new AddPeerRequest("n", "http://x:8096", token, true), CancellationToken.None)).ShouldBeOfType<BadRequestObjectResult>();
        _config.Current.Peers.ShouldBeEmpty();
        var peer = SeedPeer();
        Run(_c.UpdatePeer(peer.Id, new UpdatePeerRequest("n", "http://x:8096", token, true), CancellationToken.None)).ShouldBeOfType<BadRequestObjectResult>();
        _config.Current.Peers.Single().Token.ShouldBe(Token);
    }

    [Fact]
    public void Peer_TokenLongerThan512_IsBadRequest_512IsFine()
    {
        Run(_c.AddPeer(new AddPeerRequest("n", "http://x:8096", new string('a', 513), true), CancellationToken.None)).ShouldBeOfType<BadRequestObjectResult>();
        Run(_c.AddPeer(new AddPeerRequest("n", "http://x:8096", new string('a', 512), true), CancellationToken.None)).ShouldBeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task TestPeer_TokenWithControlCharactersOrTooLong_IsErrorWithoutNetwork()
    {
        foreach (var token in new[] { "js1_bad\ntoken", new string('a', 513) })
        {
            var response = Value<TestPeerResponse>(await _c.TestPeer(new TestPeerRequest("http://x:8096", token), CancellationToken.None));
            response.Ok.ShouldBeFalse();
            response.Error.ShouldNotBeNull().ShouldContain("token", Case.Insensitive);
        }

        _peers.PingedUrls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(101)]
    public void Shares_MissingOrOutOfRangeMaxStreams_IsBadRequest_NotSilentlyReset(int? streams)
    {
        var created = CreateShare();

        Run(_c.CreateShare(new CreateShareRequest("x", [FakeLibraries.MoviesId], streams, null), CancellationToken.None)).ShouldBeOfType<BadRequestObjectResult>();
        Run(_c.UpdateShare(created.Id, new UpdateShareRequest("bob", [FakeLibraries.MoviesId], streams, null, true), CancellationToken.None)).ShouldBeOfType<BadRequestObjectResult>();
        _config.Current.Shares.Single().MaxStreams.ShouldBe(2);
    }

    [Fact]
    public void Shares_MaxBitrateAbove2000Mbps_IsBadRequest_2000IsFine()
    {
        var created = CreateShare();

        Run(_c.CreateShare(new CreateShareRequest("x", [FakeLibraries.MoviesId], 2, 2_000_000_001), CancellationToken.None)).ShouldBeOfType<BadRequestObjectResult>();
        Run(_c.UpdateShare(created.Id, new UpdateShareRequest("bob", [FakeLibraries.MoviesId], 2, int.MaxValue, true), CancellationToken.None)).ShouldBeOfType<BadRequestObjectResult>();
        Run(_c.CreateShare(new CreateShareRequest("x", [FakeLibraries.MoviesId], 2, 2_000_000_000), CancellationToken.None)).ShouldBeOfType<OkObjectResult>();
    }

    [Fact]
    public void UpdatePeer_Unknown_IsNotFound_InvalidUrl_IsBadRequest()
    {
        Run(_c.UpdatePeer(Guid.NewGuid(), new UpdatePeerRequest("a", "http://x", null, true), CancellationToken.None)).ShouldBeOfType<NotFoundObjectResult>();
        var peer = SeedPeer();
        Run(_c.UpdatePeer(peer.Id, new UpdatePeerRequest("a", "nope", null, true), CancellationToken.None)).ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void DeletePeer_RemovesIt()
    {
        var a = SeedPeer("a", 0);
        var b = SeedPeer("b", 1);

        Run(_c.DeletePeer(a.Id, CancellationToken.None)).ShouldBeOfType<NoContentResult>();

        _config.Current.Peers.ShouldHaveSingleItem().Id.ShouldBe(b.Id);
        _saver.Saved!.Peers.ShouldHaveSingleItem().Id.ShouldBe(b.Id);
        Run(_c.DeletePeer(a.Id, CancellationToken.None)).ShouldBeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public void ReorderPeers_AssignsPrioritiesInGivenOrder_UnlistedKeepOrderAfter()
    {
        var a = SeedPeer("a", 0);
        var b = SeedPeer("b", 1);
        var c = SeedPeer("c", 2);
        var d = SeedPeer("d", 3);

        var result = Run(_c.ReorderPeers(new ReorderPeersRequest([c.Id, Guid.NewGuid(), a.Id]), CancellationToken.None));

        result.ShouldBeOfType<NoContentResult>();
        Status().Peers.Select(p => p.Name).ShouldBe(["c", "a", "b", "d"]);
        _config.Current.Peers.OrderBy(p => p.Priority).Select(p => p.Priority).ShouldBe([0, 1, 2, 3]);
        _saver.Saved!.Peers.OrderBy(p => p.Priority).Select(p => p.Name).ShouldBe(["c", "a", "b", "d"]);
        _ = (a, b, c, d);
    }

    // ---- test connection ----

    [Fact]
    public async Task TestPeer_Ok_ReportsServerAndTitleCount()
    {
        _peers.Pings = _ => new PeerPingResult(true, "Alice", "1.0", PingFailure.None, null);
        _peers.Catalogs[Guid.Empty] = _ => FakePeerClient.Ok(
            TestItems.Movie("A", tmdb: "1", id: "1"), TestItems.Movie("B", tmdb: "2", id: "2"));

        var response = Value<TestPeerResponse>(await _c.TestPeer(new TestPeerRequest(" http://alice:8096/ ", Token), CancellationToken.None));

        response.Ok.ShouldBeTrue(response.Error);
        response.ServerName.ShouldBe("Alice");
        response.ProtocolVersion.ShouldBe("1.0");
        response.Titles.ShouldBe(2);
        response.Error.ShouldBeNull();
        (_peers.PingedUrls.Single().Trim() + "/").ShouldBe("http://alice:8096/");
        _config.Current.Peers.ShouldBeEmpty();
    }

    public static IEnumerable<object[]> TestFailures() =>
    [
        [PingFailure.InvalidUrl, "http://", "valid"],
        [PingFailure.Unreachable, "http://x", "reach"],
        [PingFailure.Timeout, "http://x", "in time"],
        [PingFailure.Redirect, "http://x", "redirect"],
        [PingFailure.NotJellyShare, "http://x", "not a JellyShare"],
        [PingFailure.IncompatibleProtocol, "http://x", "ncompatible"],
    ];

    [Theory]
    [MemberData(nameof(TestFailures))]
    public async Task TestPeer_PingFailure_IsReadableError(PingFailure failure, string url, string expected)
    {
        _peers.Pings = _ => new PeerPingResult(false, null, "2.0", failure, "detail " + Token);

        var response = Value<TestPeerResponse>(await _c.TestPeer(new TestPeerRequest(url, Token), CancellationToken.None));

        response.Ok.ShouldBeFalse();
        response.Error.ShouldNotBeNull().ShouldContain(expected, Case.Insensitive);
        response.Error.ShouldNotContain(Token, Case.Sensitive);
        _peers.Fetches.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("HTTP 401", "token")]
    [InlineData("HTTP 403", "blocking")]
    [InlineData("HTTP 302 (redirects are not followed)", "redirect")]
    [InlineData("timeout", "in time")]
    [InlineData("incompatible protocol 2.0", "ncompatible")]
    [InlineData("invalid catalog", "not valid")]
    [InlineData("connection failed: refused", "connect")]
    [InlineData("HTTP 500", "HTTP 500")]
    public async Task TestPeer_CatalogFailure_IsReadableError_NeverEchoesToken(string error, string expected)
    {
        _peers.Catalogs[Guid.Empty] = _ => FakePeerClient.Fail(error + " " + Token);

        var response = Value<TestPeerResponse>(await _c.TestPeer(new TestPeerRequest("http://x:8096", Token), CancellationToken.None));

        response.Ok.ShouldBeFalse();
        response.Error.ShouldNotBeNull().ShouldContain(expected, Case.Insensitive);
        response.Error.ShouldNotContain(Token, Case.Sensitive);
        JsonSerializer.Serialize(response).ShouldNotContain(Token, Case.Sensitive);
    }

    [Fact]
    public async Task TestPeer_MissingToken_IsErrorWithoutNetwork()
    {
        var response = Value<TestPeerResponse>(await _c.TestPeer(new TestPeerRequest("http://x:8096", "  "), CancellationToken.None));

        response.Ok.ShouldBeFalse();
        response.Error.ShouldNotBeNull().ShouldContain("token", Case.Insensitive);
        _peers.PingedUrls.ShouldBeEmpty();
    }

    [Fact]
    public async Task TestPeer_SavedPeerWithoutToken_UsesStoredToken()
    {
        var peer = SeedPeer();
        _peers.Catalogs[peer.Id] = _ => FakePeerClient.Ok(TestItems.Movie("A", tmdb: "1", id: "1"));
        _peers.Catalogs[Guid.Empty] = _ => FakePeerClient.Fail("HTTP 401");

        // The fake keys catalogs by peer id, so the controller must test with the saved peer's id and stored token.
        var response = Value<TestPeerResponse>(await _c.TestPeer(new TestPeerRequest(peer.BaseUrl, "", peer.Id), CancellationToken.None));

        response.Ok.ShouldBeTrue(response.Error);
        response.Titles.ShouldBe(1);
    }

    [Theory]
    [InlineData("http://alice:8096/")]
    [InlineData("HTTP://ALICE:8096")]
    [InlineData(" http://alice:8096 ")]
    public async Task TestPeer_SavedPeer_SameUrlInAnotherSpelling_StillUsesStoredToken(string url)
    {
        var peer = SeedPeer();
        _peers.Catalogs[peer.Id] = _ => FakePeerClient.Ok(TestItems.Movie("A", tmdb: "1", id: "1"));

        var response = Value<TestPeerResponse>(await _c.TestPeer(new TestPeerRequest(url, "", peer.Id), CancellationToken.None));

        response.Ok.ShouldBeTrue(response.Error);
    }

    [Theory]
    [InlineData("http://alice-typo:8096")]
    [InlineData("http://alice:8097")]
    [InlineData("https://alice:8096")]
    [InlineData("http://alice:8096/jellyfin")]
    [InlineData("http://evil.example")]
    public async Task TestPeer_SavedPeer_EditedUrl_NeverSendsTheStoredToken(string url)
    {
        var peer = SeedPeer();
        _peers.Catalogs[peer.Id] = _ => FakePeerClient.Ok(TestItems.Movie("A", tmdb: "1", id: "1"));

        var response = Value<TestPeerResponse>(await _c.TestPeer(new TestPeerRequest(url, "", peer.Id), CancellationToken.None));

        response.Ok.ShouldBeFalse();
        response.Error.ShouldNotBeNull().ShouldContain("token", Case.Insensitive);
        response.Error.ShouldNotContain(Token, Case.Sensitive);
        _peers.PingedUrls.ShouldBeEmpty();
        _peers.Fetches.ShouldBeEmpty();
    }

    [Fact]
    public async Task TestPeer_SavedPeer_EditedUrl_WithTheTokenReEntered_Works()
    {
        var peer = SeedPeer();
        _peers.Catalogs[Guid.Empty] = _ => FakePeerClient.Ok(TestItems.Movie("A", tmdb: "1", id: "1"));

        var response = Value<TestPeerResponse>(await _c.TestPeer(new TestPeerRequest("http://other:8096", "js1_typed", peer.Id), CancellationToken.None));

        response.Ok.ShouldBeTrue(response.Error);
        _peers.PingedUrls.Single().ShouldBe("http://other:8096");
    }

    // ---- sync ----

    [Fact]
    public void Sync_StartsInBackground_Returns202_AndStatusShowsRunning()
    {
        var result = Run(_c.Sync(CancellationToken.None)).ShouldBeAssignableTo<ObjectResult>();

        result.StatusCode.ShouldBe(202);
        result.Value.ShouldBe(new SyncStartResponse(true, false));
        _sync.BackgroundStarts.ShouldBe(1);
        _sync.Runs.ShouldBe(0);
        Status().SyncRunning.ShouldBeTrue();
    }

    [Fact]
    public void Sync_WhileRunning_Returns202AlreadyRunning_AndStartsNothing()
    {
        _sync.IsRunning = true;

        var result = Run(_c.Sync(CancellationToken.None)).ShouldBeAssignableTo<ObjectResult>();

        result.StatusCode.ShouldBe(202);
        result.Value.ShouldBe(new SyncStartResponse(false, true));
        _sync.BackgroundStarts.ShouldBe(0);
    }

    [Fact]
    public void Status_ShowsLastSyncError_AsReadableText()
    {
        _sync.LastError = "JellyShare: the movie and show placeholder folders must be separate";

        Status().SyncError.ShouldNotBeNull().ShouldContain("separate", Case.Sensitive);
        Status().SyncRunning.ShouldBeFalse();
    }

    [Fact]
    public void DeletePeer_StartsOrQueuesASyncToRemoveItsPlaceholders()
    {
        var peer = SeedPeer();
        _sync.IsRunning = true;

        Run(_c.DeletePeer(peer.Id, CancellationToken.None)).ShouldBeOfType<NoContentResult>();

        _sync.LastQueueIfRunning.ShouldBe(true);
    }

    // ---- status ----

    [Fact]
    public void Status_Shape_SharesPeersPlaceholdersLibraries()
    {
        var created = CreateShare("bob", 2, 5_000_000);
        _limiter.TryAcquire(created.Id, Guid.NewGuid(), 2);
        _limiter.TryAcquire(created.Id, Guid.NewGuid(), 2);
        var alice = SeedPeer("alice", 0);
        var bob = SeedPeer("bob2", 1, enabled: false);
        var when = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
        var catalog = new CatalogResponse("1.0", "alice", "e", [TestItems.Movie("A", tmdb: "1", id: "1"), TestItems.Movie("B", tmdb: "2", id: "2"), TestItems.Movie("C", tmdb: "3", id: "3")]);
        new PeerStateStore(Path.Combine(_tmp.Sub("data"), "peer-state.json")).Save(new Dictionary<Guid, PeerState>
        {
            [alice.Id] = new() { LastGoodCatalog = catalog, LastSuccessUtc = when },
            [bob.Id] = new() { FailingSinceUtc = when, LastError = "HTTP 401" },
        });
        _sync.LastReport = new SyncReport(3, 0, 1, 2, 0, ["bob2: HTTP 401"]);

        var s = Status();

        s.ProtocolVersion.ShouldBe(ProtocolInfo.Version);
        s.ServerName.ShouldBe("Alice");
        var share = s.Shares.ShouldHaveSingleItem();
        share.Id.ShouldBe(created.Id);
        share.ActiveStreams.ShouldBe(2);
        share.MaxBitrate.ShouldBe(5_000_000);
        s.Peers.Select(p => p.Name).ShouldBe(["alice", "bob2"]);
        s.Peers[0].LastSuccessUtc.ShouldBe(when);
        s.Peers[0].Titles.ShouldBe(3);
        s.Peers[0].LastError.ShouldBeNull();
        s.Peers[1].LastError.ShouldBe("HTTP 401");
        s.Peers[1].FailingSinceUtc.ShouldBe(when);
        s.Peers[0].HasToken.ShouldBeTrue();
        s.LastSync!.Offered.ShouldBe(3);
        s.Movies.Path.ShouldBe(_config.Current.MoviesPlaceholderPath);
        s.Shows.Path.ShouldBe(_config.Current.ShowsPlaceholderPath);
        s.Movies.Attached.ShouldBeFalse();
        s.Shows.Attached.ShouldBeFalse();
        s.Libraries.Count.ShouldBe(3);
    }

    [Fact]
    public void Status_RootAttached_ReportsLibraryAndSeriesGrouping()
    {
        _libs.Libraries[0] = _libs.Libraries[0] with { Locations = ["/media/Movies", _config.Current.MoviesPlaceholderPath + Path.DirectorySeparatorChar] };
        _libs.Libraries[1] = _libs.Libraries[1] with { Locations = ["/media/Shows", _config.Current.ShowsPlaceholderPath], AutomaticSeriesGrouping = false };

        var s = Status();

        s.Movies.Attached.ShouldBeTrue();
        s.Movies.LibraryId.ShouldBe(FakeLibraries.MoviesId);
        s.Movies.LibraryName.ShouldBe("Movies");
        s.Shows.Attached.ShouldBeTrue();
        s.Shows.LibraryName.ShouldBe("Shows");
        s.Shows.AutomaticSeriesGrouping.ShouldBe(false);
    }

    [Fact]
    public void Status_NoPersistedState_StillWorks()
    {
        SeedPeer();

        var peer = Status().Peers.ShouldHaveSingleItem();

        peer.LastSuccessUtc.ShouldBeNull();
        peer.Titles.ShouldBeNull();
    }

    // ---- attach ----

    [Fact]
    public async Task Attach_Movies_AddsOnlyThePluginsOwnMoviesRoot()
    {
        var result = await _c.Attach(new AttachLibraryRequest(FakeLibraries.MoviesId, "Movies"), CancellationToken.None);

        var root = Value<PlaceholderRootView>(result);
        _libs.Added.ShouldHaveSingleItem().ShouldBe(("Movies", _config.Current.MoviesPlaceholderPath));
        root.Attached.ShouldBeTrue();
        File.Exists(Path.Combine(_config.Current.MoviesPlaceholderPath, ".jellyshare")).ShouldBeTrue();
    }

    [Fact]
    public async Task Attach_Shows_AddsShowsRoot()
    {
        await _c.Attach(new AttachLibraryRequest(FakeLibraries.ShowsId, "shows"), CancellationToken.None);

        _libs.Added.ShouldHaveSingleItem().ShouldBe(("Shows", _config.Current.ShowsPlaceholderPath));
    }

    [Theory]
    [InlineData("Music")]
    [InlineData("")]
    [InlineData("../../etc")]
    [InlineData(null)]
    public async Task Attach_UnknownType_IsBadRequest_AddsNothing(string? type)
    {
        var result = await _c.Attach(new AttachLibraryRequest(FakeLibraries.MoviesId, type), CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>();
        _libs.Added.ShouldBeEmpty();
    }

    [Fact]
    public async Task Attach_LibraryOfWrongKind_IsBadRequest()
    {
        (await _c.Attach(new AttachLibraryRequest(FakeLibraries.ShowsId, "Movies"), CancellationToken.None)).ShouldBeOfType<BadRequestObjectResult>();
        (await _c.Attach(new AttachLibraryRequest(FakeLibraries.MoviesId, "Shows"), CancellationToken.None)).ShouldBeOfType<BadRequestObjectResult>();
        (await _c.Attach(new AttachLibraryRequest(FakeLibraries.MusicId, "Movies"), CancellationToken.None)).ShouldBeOfType<BadRequestObjectResult>();
        _libs.Added.ShouldBeEmpty();
    }

    [Fact]
    public async Task Attach_UnknownLibrary_IsNotFound()
    {
        (await _c.Attach(new AttachLibraryRequest(Guid.NewGuid(), "Movies"), CancellationToken.None)).ShouldBeOfType<NotFoundObjectResult>();
        _libs.Added.ShouldBeEmpty();
    }

    [Fact]
    public async Task Attach_AlreadyAttached_IsIdempotent()
    {
        await _c.Attach(new AttachLibraryRequest(FakeLibraries.MoviesId, "Movies"), CancellationToken.None);
        var again = await _c.Attach(new AttachLibraryRequest(FakeLibraries.MoviesId, "Movies"), CancellationToken.None);

        _libs.Added.ShouldHaveSingleItem();
        Value<PlaceholderRootView>(again).Attached.ShouldBeTrue();
    }

    [Theory]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public async Task Attach_LibraryManagerFails_IsReadableError_NotAnUnhandledException(Type exceptionType)
    {
        _libs.AddThrows = (Exception)Activator.CreateInstance(exceptionType, "boom from Jellyfin")!;

        var result = await _c.Attach(new AttachLibraryRequest(FakeLibraries.MoviesId, "Movies"), CancellationToken.None);

        var obj = result.ShouldBeAssignableTo<ObjectResult>();
        obj.StatusCode.ShouldBe(500);
        var message = obj.Value.ShouldBeOfType<ErrorResponse>().Error;
        message.ShouldContain("boom from Jellyfin", Case.Sensitive);
        message.ShouldContain("Could not add", Case.Sensitive);
    }

    [Fact]
    public async Task Attach_Cancelled_StillPropagatesCancellation()
    {
        _libs.AddThrows = new OperationCanceledException();

        await ShouldExactly.ThrowAsync<OperationCanceledException>(() => _c.Attach(new AttachLibraryRequest(FakeLibraries.MoviesId, "Movies"), CancellationToken.None));
    }

    [Fact]
    public async Task Attach_RootAlreadyInAnotherLibrary_IsConflict()
    {
        _libs.Libraries.Add(new(Guid.NewGuid(), "Movies 2", "movies", [_config.Current.MoviesPlaceholderPath], false));

        var result = await _c.Attach(new AttachLibraryRequest(FakeLibraries.MoviesId, "Movies"), CancellationToken.None);

        result.ShouldBeOfType<ConflictObjectResult>();
        _libs.Added.ShouldBeEmpty();
    }

    [Fact]
    public async Task Attach_NonEmptyFolderWithoutMarker_IsConflict_AndNoMarkerIsCreated()
    {
        var root = _config.Current.MoviesPlaceholderPath;
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "owner.mkv"), "x", CancellationToken.None);

        var result = await _c.Attach(new AttachLibraryRequest(FakeLibraries.MoviesId, "Movies"), CancellationToken.None);

        ErrorOf(result.ShouldBeOfType<ConflictObjectResult>()).ShouldContain(".jellyshare", Case.Sensitive);
        File.Exists(Path.Combine(root, ".jellyshare")).ShouldBeFalse();
        _libs.Added.ShouldBeEmpty();
    }
}
