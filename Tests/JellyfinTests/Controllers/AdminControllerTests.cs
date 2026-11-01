using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JellyShare.Application.Features.Catalog;
using JellyShare.Application.Features.Streaming;
using JellyShare.Data.Files.Placeholders;
using JellyShare.Data.Files.Stores;
using JellyShare.Domain.Exceptions;
using JellyShare.Domain.Models.Placeholders;
using JellyShare.Domain.Enums;
using JellyShare.Domain.Interfaces.Configuration;
using JellyShare.Domain.Interfaces.Library;
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
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JellyShare.JellyfinTests.Controllers;

/// <summary>Like <c>PluginSettingsStore</c>: only what is passed to <c>Save</c> becomes the stored settings.</summary>
internal sealed class FakeSaver : ISettingsStore
{
    private readonly SnapshotSettings _target;

    public FakeSaver(SnapshotSettings target)
    {
        _target = target;
    }

    public int Saves { get; private set; }

    public JellyShareSettings? Saved { get; private set; }

    public void Save(JellyShareSettings settings)
    {
        Saves++;
        Saved = settings;
        _target.Store(settings);
    }
}

internal sealed class FakeLibraries : IAdminLibraries
{
    public static readonly Guid MoviesId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid ShowsId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid MusicId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    public List<LibraryInfo> Libraries { get; } = new()
    {
        new(MoviesId, "Movies", "movies", ["/media/Movies"], false),
        new(ShowsId, "Shows", "tvshows", ["/media/Shows"], true),
        new(MusicId, "Music", "music", ["/media/Music"], false),
    };

    public List<(string Library, string Path)> Added { get; } = new();

    public Exception? AddThrows { get; set; }

    public IReadOnlyList<LibraryInfo> List() => Libraries.ToList();

    public Task AddPathAsync(string libraryName, string path, CancellationToken ct)
    {
        if (AddThrows is not null)
        {
            throw AddThrows;
        }

        Added.Add((libraryName, path));
        var i = Libraries.FindIndex(l => l.Name == libraryName);
        Libraries[i] = Libraries[i] with { Locations = Libraries[i].Locations.Append(path).ToList() };
        return Task.CompletedTask;
    }
}

internal sealed class FakeSync : ISyncRunner
{
    public SyncReport? LastReport { get; set; }

    public int Runs { get; private set; }

    public Func<SyncReport> Report { get; set; } = () => new SyncReport(3, 1, 1, 2, 0, []);

    public bool IsRunning { get; set; }

    public string? LastError { get; set; }

    public int BackgroundStarts { get; private set; }

    public bool? LastQueueIfRunning { get; private set; }

    public Task<SyncReport> RunAsync(CancellationToken ct)
    {
        Runs++;
        LastReport = Report();
        return Task.FromResult(LastReport);
    }

    public bool TryStartBackground(bool queueIfRunning = false)
    {
        LastQueueIfRunning = queueIfRunning;
        if (IsRunning)
        {
            return false;
        }

        BackgroundStarts++;
        IsRunning = true;
        return true;
    }
}

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
    private readonly AdminController _c;
    private readonly Microsoft.AspNetCore.Http.DefaultHttpContext _http = new();

    public AdminControllerTests()
    {
        _saver = new FakeSaver(_config);
        _config.Settings.MoviesPlaceholderPath = _tmp.Sub("ph", "Movies");
        _config.Settings.ShowsPlaceholderPath = _tmp.Sub("ph", "Shows");
        _index = new ConsumerIndexFiles(() => _tmp.Sub("data"));
        _c = new AdminController(
            _config, _saver, _tokens, _limiter, _peers, _sync, _index, _libs, new PlaceholderWriter(), new FakeIdentity(), NullLogger<AdminController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = _http },
        };
    }

    [Fact]
    public void CreateShare_ResponseIsNotCacheable()
    {
        CreateShare();

        Assert.Equal("no-store", _http.Response.Headers.CacheControl.ToString());
    }

    public void Dispose() => _tmp.Dispose();

    private static T Value<T>(IActionResult result)
        => Assert.IsType<T>(Assert.IsType<OkObjectResult>(result).Value);

    private static string ErrorOf(IActionResult result)
    {
        var value = result switch
        {
            BadRequestObjectResult b => b.Value,
            NotFoundObjectResult n => n.Value,
            ObjectResult o => o.Value,
            _ => null,
        };
        return Assert.IsType<ErrorResponse>(value).Error;
    }

    private CreatedShareResponse CreateShare(string name = "bob", int streams = 2, int? bitrate = null, params Guid[] libs)
        => Value<CreatedShareResponse>(_c.CreateShare(new CreateShareRequest(name, libs.Length == 0 ? [FakeLibraries.MoviesId] : libs.ToList(), streams, bitrate)));

    private static string Sha256Hex(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private StatusResponse Status() => Value<StatusResponse>(_c.Status());

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
        Assert.NotNull(policy);
        Assert.Equal(Policies.RequiresElevation, policy!.Policy);
        Assert.Empty(type.GetCustomAttributes<AllowAnonymousAttribute>());

        var actions = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any())
            .ToList();
        Assert.True(actions.Count >= 11);
        foreach (var action in actions)
        {
            Assert.Empty(action.GetCustomAttributes<AllowAnonymousAttribute>());
        }

        Assert.All(type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly), m =>
            Assert.True(m.GetCustomAttributes<HttpMethodAttribute>().Any(), m.Name + " is public but has no route"));
    }

    // ---- shares ----

    [Fact]
    public void CreateShare_ReturnsTokenOnce_StoresOnlyHash()
    {
        var created = CreateShare("bob", 3, 8_000_000);

        Assert.StartsWith("js1_", created.Token, StringComparison.Ordinal);
        var share = Assert.Single(_config.Current.Shares);
        Assert.Equal(created.Id, share.Id);
        Assert.Equal("bob", share.Name);
        Assert.Equal(Sha256Hex(created.Token), share.TokenHash);
        Assert.NotEqual(created.Token, share.TokenHash);
        Assert.Equal(3, share.MaxStreams);
        Assert.Equal(8_000_000, share.MaxBitrate);
        Assert.True(share.Enabled);
        Assert.Equal([FakeLibraries.MoviesId], share.LibraryIds);
        Assert.NotNull(_tokens.Verify(created.Token, _config.Current.Shares));
        Assert.True(_saver.Saves >= 1);
        Assert.Equal(created.Id, Assert.Single(_saver.Saved!.Shares).Id);
    }

    [Fact]
    public void CreateShare_TwoCalls_GiveDifferentTokens()
    {
        Assert.NotEqual(CreateShare("a").Token, CreateShare("b").Token);
    }

    [Fact]
    public void Status_NeverContainsTokenOrHash()
    {
        var created = CreateShare();
        var json = JsonSerializer.Serialize(Status());

        Assert.DoesNotContain(created.Token, json, StringComparison.Ordinal);
        Assert.DoesNotContain(Sha256Hex(created.Token), json, StringComparison.Ordinal);
        Assert.DoesNotContain("TokenHash", json, StringComparison.OrdinalIgnoreCase);
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
        var result = _c.CreateShare(new CreateShareRequest(name, [FakeLibraries.MoviesId], streams, bitrate));

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(_config.Current.Shares);
        Assert.Equal(0, _saver.Saves);
    }

    [Fact]
    public void CreateShare_UnknownLibrary_IsBadRequest()
    {
        var result = _c.CreateShare(new CreateShareRequest("bob", [Guid.NewGuid()], 2, null));

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(_config.Current.Shares);
    }

    [Fact]
    public void CreateShare_NullBody_IsBadRequest()
    {
        Assert.IsType<BadRequestObjectResult>(_c.CreateShare(null!));
    }

    [Fact]
    public void UpdateShare_ChangesNameLibrariesLimitsEnabled_KeepsToken()
    {
        var created = CreateShare();
        var hashBefore = _config.Current.Shares[0].TokenHash;

        var result = _c.UpdateShare(created.Id, new UpdateShareRequest("bobby", [FakeLibraries.ShowsId, FakeLibraries.MoviesId], 5, 4_000_000, false));

        var view = Value<ShareView>(result);
        var share = _config.Current.Shares.Single();
        Assert.Equal("bobby", share.Name);
        Assert.Equal([FakeLibraries.ShowsId, FakeLibraries.MoviesId], share.LibraryIds);
        Assert.Equal(5, share.MaxStreams);
        Assert.Equal(4_000_000, share.MaxBitrate);
        Assert.False(share.Enabled);
        Assert.Equal(hashBefore, share.TokenHash);
        Assert.Equal("bobby", Assert.Single(_saver.Saved!.Shares).Name);
        Assert.Equal(created.Id, view.Id);
        Assert.False(view.Enabled);
        Assert.Null(_tokens.Verify(created.Token, _config.Current.Shares));

        _c.UpdateShare(created.Id, new UpdateShareRequest("bobby", [FakeLibraries.MoviesId], 5, null, true));
        Assert.NotNull(_tokens.Verify(created.Token, _config.Current.Shares));
        Assert.Null(_config.Current.Shares.Single().MaxBitrate);
    }

    [Fact]
    public void UpdateShare_Unknown_IsNotFound()
    {
        var result = _c.UpdateShare(Guid.NewGuid(), new UpdateShareRequest("x", [], 2, null, true));
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public void UpdateShare_Invalid_ChangesNothing()
    {
        var created = CreateShare();
        var saves = _saver.Saves;

        var result = _c.UpdateShare(created.Id, new UpdateShareRequest("", [FakeLibraries.MoviesId], 2, null, true));

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("bob", _config.Current.Shares.Single().Name);
        Assert.Equal(saves, _saver.Saves);
    }

    [Fact]
    public void RevokeShare_RemovesItAndTheTokenStopsWorking()
    {
        var created = CreateShare();
        var other = CreateShare("carol");
        Assert.NotNull(_tokens.Verify(created.Token, _config.Current.Shares));

        var result = _c.DeleteShare(created.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.Null(_tokens.Verify(created.Token, _config.Current.Shares));
        Assert.NotNull(_tokens.Verify(other.Token, _config.Current.Shares));
        Assert.Equal(other.Id, Assert.Single(_config.Current.Shares).Id);
        Assert.Equal(other.Id, Assert.Single(_saver.Saved!.Shares).Id);
    }

    [Fact]
    public void RevokeShare_Unknown_IsNotFound()
    {
        Assert.IsType<NotFoundObjectResult>(_c.DeleteShare(Guid.NewGuid()));
    }

    // ---- peers ----

    [Fact]
    public void AddPeer_NormalizesUrl_AppendsPriority_NeverReturnsToken()
    {
        SeedPeer("alice", 4);

        var result = _c.AddPeer(new AddPeerRequest("  carol ", " http://carol:8096/jellyfin/ ", Token, null));

        var view = Value<PeerView>(result);
        Assert.Equal("carol", view.Name);
        Assert.Equal("http://carol:8096/jellyfin", view.BaseUrl);
        Assert.True(view.HasToken);
        Assert.True(view.Enabled);
        Assert.Equal(5, view.Priority);
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(view), StringComparison.Ordinal);
        var stored = _config.Current.Peers.Single(p => p.Name == "carol");
        Assert.Equal(Token, stored.Token);
        Assert.NotEqual(Guid.Empty, stored.Id);
        Assert.Equal(view.Id, stored.Id);
        Assert.True(_saver.Saves >= 1);
        Assert.Contains(_saver.Saved!.Peers, p => p.Id == view.Id);
    }

    [Theory]
    [InlineData("", "http://x:8096", Token)]
    [InlineData("n", "not a url", Token)]
    [InlineData("n", "ftp://x/", Token)]
    [InlineData("n", "http://x:8096", "")]
    [InlineData("n", "http://x:8096", "   ")]
    public void AddPeer_Invalid_IsBadRequest(string name, string url, string token)
    {
        var result = _c.AddPeer(new AddPeerRequest(name, url, token, true));

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(_config.Current.Peers);
    }

    [Fact]
    public void UpdatePeer_EmptyToken_KeepsStoredToken()
    {
        var peer = SeedPeer();

        var result = _c.UpdatePeer(peer.Id, new UpdatePeerRequest("alice2", "http://alice2:8096/", "", false));

        var view = Value<PeerView>(result);
        var stored = _config.Current.Peers.Single();
        Assert.Equal("alice2", stored.Name);
        Assert.Equal("http://alice2:8096", stored.BaseUrl);
        Assert.False(stored.Enabled);
        Assert.Equal(Token, stored.Token);
        Assert.Equal(peer.Priority, stored.Priority);
        Assert.False(view.Enabled);
    }

    [Fact]
    public void UpdatePeer_NewToken_ReplacesIt()
    {
        var peer = SeedPeer();

        _c.UpdatePeer(peer.Id, new UpdatePeerRequest("alice", peer.BaseUrl, "js1_new", true));

        Assert.Equal("js1_new", _config.Current.Peers.Single().Token);
        Assert.Equal("js1_new", Assert.Single(_saver.Saved!.Peers).Token);
    }

    [Fact]
    public void UpdatePeer_IsCopyOnWrite_ARunningSyncKeepsASelfConsistentPeer()
    {
        var peer = SeedPeer();
        var listBefore = _config.Current.Peers;

        _c.UpdatePeer(peer.Id, new UpdatePeerRequest("renamed", "http://other:8096", "js1_new", false));

        Assert.Equal("alice", peer.Name);
        Assert.Equal("http://alice:8096", peer.BaseUrl);
        Assert.Equal(Token, peer.Token);
        Assert.True(peer.Enabled);
        Assert.NotSame(listBefore, _config.Current.Peers);
        Assert.NotSame(peer, _config.Current.Peers.Single());
        Assert.Equal("renamed", Assert.Single(_saver.Saved!.Peers).Name);
    }

    [Fact]
    public void UpdateShare_IsCopyOnWrite()
    {
        var created = CreateShare();
        var before = _config.Current.Shares.Single();

        _c.UpdateShare(created.Id, new UpdateShareRequest("renamed", [FakeLibraries.ShowsId], 7, 1_000_000, false));

        Assert.Equal("bob", before.Name);
        Assert.Equal(2, before.MaxStreams);
        Assert.True(before.Enabled);
        Assert.NotSame(before, _config.Current.Shares.Single());
        Assert.Equal("renamed", _config.Current.Shares.Single().Name);
        Assert.Equal("renamed", Assert.Single(_saver.Saved!.Shares).Name);
    }

    [Fact]
    public void ReorderPeers_IsCopyOnWrite()
    {
        var a = SeedPeer("a", 0);
        var b = SeedPeer("b", 1);

        _c.ReorderPeers(new ReorderPeersRequest([b.Id, a.Id]));

        Assert.Equal(0, a.Priority);
        Assert.Equal(1, b.Priority);
        Assert.Equal(["b", "a"], Status().Peers.Select(p => p.Name));
    }

    [Theory]
    [InlineData("js1_bad\ntoken")]
    [InlineData("js1_bad\u0000token")]
    public void Peer_TokenWithControlCharacters_IsBadRequest(string token)
    {
        Assert.IsType<BadRequestObjectResult>(_c.AddPeer(new AddPeerRequest("n", "http://x:8096", token, true)));
        Assert.Empty(_config.Current.Peers);
        var peer = SeedPeer();
        Assert.IsType<BadRequestObjectResult>(_c.UpdatePeer(peer.Id, new UpdatePeerRequest("n", "http://x:8096", token, true)));
        Assert.Equal(Token, _config.Current.Peers.Single().Token);
    }

    [Fact]
    public void Peer_TokenLongerThan512_IsBadRequest_512IsFine()
    {
        Assert.IsType<BadRequestObjectResult>(_c.AddPeer(new AddPeerRequest("n", "http://x:8096", new string('a', 513), true)));
        Assert.IsType<OkObjectResult>(_c.AddPeer(new AddPeerRequest("n", "http://x:8096", new string('a', 512), true)));
    }

    [Fact]
    public async Task TestPeer_TokenWithControlCharactersOrTooLong_IsErrorWithoutNetwork()
    {
        foreach (var token in new[] { "js1_bad\ntoken", new string('a', 513) })
        {
            var response = Value<TestPeerResponse>(await _c.TestPeer(new TestPeerRequest("http://x:8096", token), CancellationToken.None));
            Assert.False(response.Ok);
            Assert.Contains("token", response.Error, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Empty(_peers.PingedUrls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(101)]
    public void Shares_MissingOrOutOfRangeMaxStreams_IsBadRequest_NotSilentlyReset(int? streams)
    {
        var created = CreateShare();

        Assert.IsType<BadRequestObjectResult>(_c.CreateShare(new CreateShareRequest("x", [FakeLibraries.MoviesId], streams, null)));
        Assert.IsType<BadRequestObjectResult>(_c.UpdateShare(created.Id, new UpdateShareRequest("bob", [FakeLibraries.MoviesId], streams, null, true)));
        Assert.Equal(2, _config.Current.Shares.Single().MaxStreams);
    }

    [Fact]
    public void Shares_MaxBitrateAbove2000Mbps_IsBadRequest_2000IsFine()
    {
        var created = CreateShare();

        Assert.IsType<BadRequestObjectResult>(_c.CreateShare(new CreateShareRequest("x", [FakeLibraries.MoviesId], 2, 2_000_000_001)));
        Assert.IsType<BadRequestObjectResult>(_c.UpdateShare(created.Id, new UpdateShareRequest("bob", [FakeLibraries.MoviesId], 2, int.MaxValue, true)));
        Assert.IsType<OkObjectResult>(_c.CreateShare(new CreateShareRequest("x", [FakeLibraries.MoviesId], 2, 2_000_000_000)));
    }

    [Fact]
    public void UpdatePeer_Unknown_IsNotFound_InvalidUrl_IsBadRequest()
    {
        Assert.IsType<NotFoundObjectResult>(_c.UpdatePeer(Guid.NewGuid(), new UpdatePeerRequest("a", "http://x", null, true)));
        var peer = SeedPeer();
        Assert.IsType<BadRequestObjectResult>(_c.UpdatePeer(peer.Id, new UpdatePeerRequest("a", "nope", null, true)));
    }

    [Fact]
    public void DeletePeer_RemovesIt()
    {
        var a = SeedPeer("a", 0);
        var b = SeedPeer("b", 1);

        Assert.IsType<NoContentResult>(_c.DeletePeer(a.Id));

        Assert.Equal(b.Id, Assert.Single(_config.Current.Peers).Id);
        Assert.Equal(b.Id, Assert.Single(_saver.Saved!.Peers).Id);
        Assert.IsType<NotFoundObjectResult>(_c.DeletePeer(a.Id));
    }

    [Fact]
    public void ReorderPeers_AssignsPrioritiesInGivenOrder_UnlistedKeepOrderAfter()
    {
        var a = SeedPeer("a", 0);
        var b = SeedPeer("b", 1);
        var c = SeedPeer("c", 2);
        var d = SeedPeer("d", 3);

        var result = _c.ReorderPeers(new ReorderPeersRequest([c.Id, Guid.NewGuid(), a.Id]));

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(["c", "a", "b", "d"], Status().Peers.Select(p => p.Name));
        Assert.Equal([0, 1, 2, 3], _config.Current.Peers.OrderBy(p => p.Priority).Select(p => p.Priority));
        Assert.Equal(["c", "a", "b", "d"], _saver.Saved!.Peers.OrderBy(p => p.Priority).Select(p => p.Name));
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

        Assert.True(response.Ok, response.Error);
        Assert.Equal("Alice", response.ServerName);
        Assert.Equal("1.0", response.ProtocolVersion);
        Assert.Equal(2, response.Titles);
        Assert.Null(response.Error);
        Assert.Equal("http://alice:8096/", _peers.PingedUrls.Single().Trim() + "/");
        Assert.Empty(_config.Current.Peers);
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

        Assert.False(response.Ok);
        Assert.Contains(expected, response.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Token, response.Error!, StringComparison.Ordinal);
        Assert.Empty(_peers.Fetches);
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

        Assert.False(response.Ok);
        Assert.Contains(expected, response.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Token, response.Error!, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestPeer_MissingToken_IsErrorWithoutNetwork()
    {
        var response = Value<TestPeerResponse>(await _c.TestPeer(new TestPeerRequest("http://x:8096", "  "), CancellationToken.None));

        Assert.False(response.Ok);
        Assert.Contains("token", response.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_peers.PingedUrls);
    }

    [Fact]
    public async Task TestPeer_SavedPeerWithoutToken_UsesStoredToken()
    {
        var peer = SeedPeer();
        _peers.Catalogs[peer.Id] = _ => FakePeerClient.Ok(TestItems.Movie("A", tmdb: "1", id: "1"));
        _peers.Catalogs[Guid.Empty] = _ => FakePeerClient.Fail("HTTP 401");

        // The fake keys catalogs by peer id, so the controller must test with the saved peer's id and stored token.
        var response = Value<TestPeerResponse>(await _c.TestPeer(new TestPeerRequest(peer.BaseUrl, "", peer.Id), CancellationToken.None));

        Assert.True(response.Ok, response.Error);
        Assert.Equal(1, response.Titles);
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

        Assert.True(response.Ok, response.Error);
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

        Assert.False(response.Ok);
        Assert.Contains("token", response.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Token, response.Error!, StringComparison.Ordinal);
        Assert.Empty(_peers.PingedUrls);
        Assert.Empty(_peers.Fetches);
    }

    [Fact]
    public async Task TestPeer_SavedPeer_EditedUrl_WithTheTokenReEntered_Works()
    {
        var peer = SeedPeer();
        _peers.Catalogs[Guid.Empty] = _ => FakePeerClient.Ok(TestItems.Movie("A", tmdb: "1", id: "1"));

        var response = Value<TestPeerResponse>(await _c.TestPeer(new TestPeerRequest("http://other:8096", "js1_typed", peer.Id), CancellationToken.None));

        Assert.True(response.Ok, response.Error);
        Assert.Equal("http://other:8096", _peers.PingedUrls.Single());
    }

    // ---- sync ----

    [Fact]
    public void Sync_StartsInBackground_Returns202_AndStatusShowsRunning()
    {
        var result = Assert.IsAssignableFrom<ObjectResult>(_c.Sync());

        Assert.Equal(202, result.StatusCode);
        Assert.Equal(new SyncStartResponse(true, false), result.Value);
        Assert.Equal(1, _sync.BackgroundStarts);
        Assert.Equal(0, _sync.Runs);
        Assert.True(Status().SyncRunning);
    }

    [Fact]
    public void Sync_WhileRunning_Returns202AlreadyRunning_AndStartsNothing()
    {
        _sync.IsRunning = true;

        var result = Assert.IsAssignableFrom<ObjectResult>(_c.Sync());

        Assert.Equal(202, result.StatusCode);
        Assert.Equal(new SyncStartResponse(false, true), result.Value);
        Assert.Equal(0, _sync.BackgroundStarts);
    }

    [Fact]
    public void Status_ShowsLastSyncError_AsReadableText()
    {
        _sync.LastError = "JellyShare: the movie and show placeholder folders must be separate";

        Assert.Contains("separate", Status().SyncError, StringComparison.Ordinal);
        Assert.False(Status().SyncRunning);
    }

    [Fact]
    public void DeletePeer_StartsOrQueuesASyncToRemoveItsPlaceholders()
    {
        var peer = SeedPeer();
        _sync.IsRunning = true;

        Assert.IsType<NoContentResult>(_c.DeletePeer(peer.Id));

        Assert.True(_sync.LastQueueIfRunning);
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

        Assert.Equal(ProtocolInfo.Version, s.ProtocolVersion);
        Assert.Equal("Alice", s.ServerName);
        var share = Assert.Single(s.Shares);
        Assert.Equal(created.Id, share.Id);
        Assert.Equal(2, share.ActiveStreams);
        Assert.Equal(5_000_000, share.MaxBitrate);
        Assert.Equal(["alice", "bob2"], s.Peers.Select(p => p.Name));
        Assert.Equal(when, s.Peers[0].LastSuccessUtc);
        Assert.Equal(3, s.Peers[0].Titles);
        Assert.Null(s.Peers[0].LastError);
        Assert.Equal("HTTP 401", s.Peers[1].LastError);
        Assert.Equal(when, s.Peers[1].FailingSinceUtc);
        Assert.True(s.Peers[0].HasToken);
        Assert.Equal(3, s.LastSync!.Offered);
        Assert.Equal(_config.Current.MoviesPlaceholderPath, s.Movies.Path);
        Assert.Equal(_config.Current.ShowsPlaceholderPath, s.Shows.Path);
        Assert.False(s.Movies.Attached);
        Assert.False(s.Shows.Attached);
        Assert.Equal(3, s.Libraries.Count);
    }

    [Fact]
    public void Status_RootAttached_ReportsLibraryAndSeriesGrouping()
    {
        _libs.Libraries[0] = _libs.Libraries[0] with { Locations = ["/media/Movies", _config.Current.MoviesPlaceholderPath + Path.DirectorySeparatorChar] };
        _libs.Libraries[1] = _libs.Libraries[1] with { Locations = ["/media/Shows", _config.Current.ShowsPlaceholderPath], AutomaticSeriesGrouping = false };

        var s = Status();

        Assert.True(s.Movies.Attached);
        Assert.Equal(FakeLibraries.MoviesId, s.Movies.LibraryId);
        Assert.Equal("Movies", s.Movies.LibraryName);
        Assert.True(s.Shows.Attached);
        Assert.Equal("Shows", s.Shows.LibraryName);
        Assert.False(s.Shows.AutomaticSeriesGrouping);
    }

    [Fact]
    public void Status_NoPersistedState_StillWorks()
    {
        SeedPeer();

        var peer = Assert.Single(Status().Peers);

        Assert.Null(peer.LastSuccessUtc);
        Assert.Null(peer.Titles);
    }

    // ---- attach ----

    [Fact]
    public async Task Attach_Movies_AddsOnlyThePluginsOwnMoviesRoot()
    {
        var result = await _c.Attach(new AttachLibraryRequest(FakeLibraries.MoviesId, "Movies"), CancellationToken.None);

        var root = Value<PlaceholderRootView>(result);
        Assert.Equal(("Movies", _config.Current.MoviesPlaceholderPath), Assert.Single(_libs.Added));
        Assert.True(root.Attached);
        Assert.True(File.Exists(Path.Combine(_config.Current.MoviesPlaceholderPath, ".jellyshare")));
    }

    [Fact]
    public async Task Attach_Shows_AddsShowsRoot()
    {
        await _c.Attach(new AttachLibraryRequest(FakeLibraries.ShowsId, "shows"), CancellationToken.None);

        Assert.Equal(("Shows", _config.Current.ShowsPlaceholderPath), Assert.Single(_libs.Added));
    }

    [Theory]
    [InlineData("Music")]
    [InlineData("")]
    [InlineData("../../etc")]
    [InlineData(null)]
    public async Task Attach_UnknownType_IsBadRequest_AddsNothing(string? type)
    {
        var result = await _c.Attach(new AttachLibraryRequest(FakeLibraries.MoviesId, type), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(_libs.Added);
    }

    [Fact]
    public async Task Attach_LibraryOfWrongKind_IsBadRequest()
    {
        Assert.IsType<BadRequestObjectResult>(await _c.Attach(new AttachLibraryRequest(FakeLibraries.ShowsId, "Movies"), CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await _c.Attach(new AttachLibraryRequest(FakeLibraries.MoviesId, "Shows"), CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await _c.Attach(new AttachLibraryRequest(FakeLibraries.MusicId, "Movies"), CancellationToken.None));
        Assert.Empty(_libs.Added);
    }

    [Fact]
    public async Task Attach_UnknownLibrary_IsNotFound()
    {
        Assert.IsType<NotFoundObjectResult>(await _c.Attach(new AttachLibraryRequest(Guid.NewGuid(), "Movies"), CancellationToken.None));
        Assert.Empty(_libs.Added);
    }

    [Fact]
    public async Task Attach_AlreadyAttached_IsIdempotent()
    {
        await _c.Attach(new AttachLibraryRequest(FakeLibraries.MoviesId, "Movies"), CancellationToken.None);
        var again = await _c.Attach(new AttachLibraryRequest(FakeLibraries.MoviesId, "Movies"), CancellationToken.None);

        Assert.Single(_libs.Added);
        Assert.True(Value<PlaceholderRootView>(again).Attached);
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

        var obj = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(500, obj.StatusCode);
        var message = Assert.IsType<ErrorResponse>(obj.Value).Error;
        Assert.Contains("boom from Jellyfin", message, StringComparison.Ordinal);
        Assert.Contains("Could not add", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Attach_Cancelled_StillPropagatesCancellation()
    {
        _libs.AddThrows = new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _c.Attach(new AttachLibraryRequest(FakeLibraries.MoviesId, "Movies"), CancellationToken.None));
    }

    [Fact]
    public async Task Attach_RootAlreadyInAnotherLibrary_IsConflict()
    {
        _libs.Libraries.Add(new(Guid.NewGuid(), "Movies 2", "movies", [_config.Current.MoviesPlaceholderPath], false));

        var result = await _c.Attach(new AttachLibraryRequest(FakeLibraries.MoviesId, "Movies"), CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
        Assert.Empty(_libs.Added);
    }

    [Fact]
    public async Task Attach_NonEmptyFolderWithoutMarker_IsConflict_AndNoMarkerIsCreated()
    {
        var root = _config.Current.MoviesPlaceholderPath;
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "owner.mkv"), "x");

        var result = await _c.Attach(new AttachLibraryRequest(FakeLibraries.MoviesId, "Movies"), CancellationToken.None);

        Assert.Contains(".jellyshare", ErrorOf(Assert.IsType<ConflictObjectResult>(result)), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root, ".jellyshare")));
        Assert.Empty(_libs.Added);
    }
}
