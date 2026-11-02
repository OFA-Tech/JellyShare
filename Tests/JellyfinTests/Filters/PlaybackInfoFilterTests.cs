using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Json;
using System.Text.Json;
using JellyShare.Application.Features.Relay;
using JellyShare.Domain.Enums;
using JellyShare.Domain.Interfaces.Configuration;
using JellyShare.Domain.Interfaces.Relay;
using JellyShare.Domain.Interfaces.Sync;
using JellyShare.Domain.Keys;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Protocol;
using JellyShare.Domain.Models.Sync;
using JellyShare.Jellyfin.Adapters;
using JellyShare.Jellyfin.Filters;
using JellyShare.UnitTests.Fakes;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JellyShare.JellyfinTests.Filters;

/// <summary>
/// Runs <see cref="PlaybackInfoFilter"/> in a real MVC pipeline against a controller with the same routes and
/// parameter names as Jellyfin 12.4's <c>MediaInfoController</c> (which copies query values over the body).
/// </summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "xUnit disposes the host and client through IAsyncLifetime.DisposeAsync.")]
public sealed class PlaybackInfoFilterTests : IAsyncLifetime
{
    internal const string RelayUrl = "http://127.0.0.1:8096/JellyShare/v1/play/movie%3Atmdb%3A10378";
    internal const string LocalPath = "/media/Movies/Sintel (2010)/Sintel (2010).mkv";
    internal const string OtherHttp = "http://example.org/video.mkv";

    internal static readonly Guid Placeholder = Guid.Parse("11111111-0000-0000-0000-000000000001");
    internal static readonly Guid Local = Guid.Parse("22222222-0000-0000-0000-000000000002");
    internal static readonly Guid OtherStrm = Guid.Parse("33333333-0000-0000-0000-000000000003");

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private const string Key = "movie:tmdb:10378";
    private const string OtherKey = "movie:tmdb:other";

    private readonly FakePlaceholders _placeholders = new();
    private readonly FakeAuthorization _auth = new();
    private readonly ConsumerConfig _config = new();
    private readonly FakeIndex _index = new();
    private readonly RelaySessions _sessions = new(new MutableClock());
    private readonly FakeStopper _stopper = new();
    private readonly LevelLogger<PlaybackInfoFilter> _filterLog = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _placeholders.Items[Placeholder] = new PlaceholderMedia(Key, 1_312_543);
        _config.Settings.Peers.Add(new RemotePeer { Id = TestItems.PeerA, Name = "alice", BaseUrl = "http://alice:8096", Token = "t", Kind = PeerKind.Account, MaxStreams = 1 });
        _index.Entries[Key] = new MergedEntry(
            Key,
            new[] { Key },
            new List<(Guid, CatalogItem)> { (TestItems.PeerA, TestItems.Movie("Sintel", tmdb: "10378", id: "alice-item")) });

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(FakeMediaInfoController).Assembly);
        builder.Services.AddSingleton<IPlaceholderLookup>(_placeholders);
        builder.Services.AddSingleton<IAuthorizationContext>(_auth);
        builder.Services.AddSingleton<IRelayBase>(new FixedRelay());
        builder.Services.AddSingleton<ISettingsProvider>(_config);
        builder.Services.AddSingleton<IConsumerIndex>(_index);
        builder.Services.AddSingleton(_sessions);
        builder.Services.AddSingleton<IPlaybackStopper>(_stopper);
        builder.Services.AddSingleton<ILogger<PlaybackInfoFilter>>(_filterLog);
        PlaybackInfoFilter.Register(builder.Services);
        _app = builder.Build();
        _app.MapControllers();
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    private async Task<PlaybackInfoResponse> Post(Guid item, object? body, string query = "")
    {
        var response = await _client.PostAsJsonAsync("/Items/" + item + "/PlaybackInfo" + query, body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PlaybackInfoResponse>(_json))!;
    }

    private async Task<T> Get<T>(string url)
    {
        var response = await _client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(_json))!;
    }

    private static object Body(int? maxStreamingBitrate = null, int? profileMax = null)
        => new { MaxStreamingBitrate = maxStreamingBitrate, DeviceProfile = new { MaxStreamingBitrate = profileMax } };

    [Fact]
    public async Task Filter_HidesRelayPathAndDisablesDirectPlay()
    {
        var info = await Post(Placeholder, Body());

        var source = info.MediaSources.ShouldHaveSingleItem();
        source.Path.ShouldBe(string.Empty);
        source.SupportsDirectPlay.ShouldBeFalse();
        source.SupportsDirectStream.ShouldBeTrue();
        source.SupportsTranscoding.ShouldBeTrue();
        source.TranscodingUrl.ShouldBeNull();
    }

    [Fact]
    public async Task Filter_LowMaxBitrate_DisablesDirectStream()
    {
        var info = await Post(Placeholder, Body(maxStreamingBitrate: 1_000_000));

        var source = info.MediaSources.ShouldHaveSingleItem();
        source.SupportsDirectPlay.ShouldBeFalse();
        source.SupportsDirectStream.ShouldBeFalse();
        source.TranscodingUrl.ShouldBe("/transcode?bitrate=1000000");
        source.Path.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task Filter_LowMaxBitrateInQuery_DisablesDirectStream()
    {
        var info = await Post(Placeholder, Body(maxStreamingBitrate: 50_000_000), "?maxStreamingBitrate=1000000");

        info.MediaSources.ShouldHaveSingleItem().SupportsDirectStream.ShouldBeFalse();
    }

    [Fact]
    public async Task Filter_LowDeviceProfileBitrate_DisablesDirectStream()
    {
        var info = await Post(Placeholder, Body(profileMax: 800_000));

        info.MediaSources.ShouldHaveSingleItem().SupportsDirectStream.ShouldBeFalse();
    }

    [Fact]
    public async Task Filter_RemoteUserLimitBelowSource_DisablesDirectStream()
    {
        _placeholders.RemoteLimit = 1_000_000;

        var info = await Post(Placeholder, Body(maxStreamingBitrate: 120_000_000));

        info.MediaSources.ShouldHaveSingleItem().SupportsDirectStream.ShouldBeFalse();
    }

    [Fact]
    public async Task Filter_PassesTheCallersDeviceToTheLookup()
    {
        await Post(Placeholder, Body(maxStreamingBitrate: 120_000_000));

        var caller = _placeholders.Callers.ShouldHaveSingleItem();
        caller.DeviceId.ShouldBe("device-1");
        caller.RemoteAddress.ShouldNotBeNull();
    }

    [Fact]
    public async Task Filter_UnreadableAuthorization_AppliesNoCallerLimits()
    {
        _auth.Fail = true;
        _placeholders.RemoteLimit = 1_000_000;

        var info = await Post(Placeholder, Body(maxStreamingBitrate: 120_000_000));

        info.MediaSources.ShouldHaveSingleItem().SupportsDirectStream.ShouldBeTrue();
        _placeholders.Callers.ShouldBeEmpty();
    }

    [Fact]
    public async Task Filter_NoBodyProfile_UsesStoredDeviceProfileBitrate()
    {
        _placeholders.StoredProfileBitrate = 800_000;

        var info = await Post(Placeholder, new { MaxStreamingBitrate = (int?)null });

        info.MediaSources.ShouldHaveSingleItem().SupportsDirectStream.ShouldBeFalse();
    }

    [Fact]
    public async Task Filter_NoBody_UsesStoredDeviceProfileBitrate()
    {
        _placeholders.StoredProfileBitrate = 800_000;

        var info = await Post(Placeholder, null);

        info.MediaSources.ShouldHaveSingleItem().SupportsDirectStream.ShouldBeFalse();
    }

    [Fact]
    public async Task Filter_BodyProfile_WinsOverStoredProfile()
    {
        _placeholders.StoredProfileBitrate = 800_000;

        var info = await Post(Placeholder, Body(profileMax: 120_000_000));

        info.MediaSources.ShouldHaveSingleItem().SupportsDirectStream.ShouldBeTrue();
    }

    [Fact]
    public async Task Filter_RequestedBitrate_WinsOverStoredProfile()
    {
        _placeholders.StoredProfileBitrate = 800_000;

        var info = await Post(Placeholder, new { MaxStreamingBitrate = 120_000_000 });

        info.MediaSources.ShouldHaveSingleItem().SupportsDirectStream.ShouldBeTrue();
    }

    [Fact]
    public async Task Filter_LazyItemEnumerable_HidesRelayPathAndEnumeratesOnce()
    {
        FakeMediaInfoController.LazyEnumerations = 0;

        var list = await Get<BaseItemDto[]>("/Users/" + Guid.NewGuid() + "/Items/" + Placeholder + "/SpecialFeatures");

        list.Select(i => i.MediaSources.Single().Path).ShouldBe(new[] { string.Empty, LocalPath, OtherHttp });
        list[0].MediaSources.Single().SupportsDirectPlay.ShouldBeFalse();
        FakeMediaInfoController.LazyEnumerations.ShouldBe(1);
    }

    [Fact]
    public async Task Filter_HighMaxBitrate_KeepsDirectStream()
    {
        var info = await Post(Placeholder, Body(maxStreamingBitrate: 120_000_000));

        var source = info.MediaSources.ShouldHaveSingleItem();
        source.SupportsDirectPlay.ShouldBeFalse();
        source.SupportsDirectStream.ShouldBeTrue();
        source.TranscodingUrl.ShouldBeNull();
    }

    [Fact]
    public async Task Filter_UnknownSourceBitrate_KeepsDirectStream()
    {
        _placeholders.Items[Placeholder] = new PlaceholderMedia("movie:tmdb:10378", null);

        var info = await Post(Placeholder, Body(maxStreamingBitrate: 1_000_000));

        info.MediaSources.ShouldHaveSingleItem().SupportsDirectStream.ShouldBeTrue();
    }

    [Fact]
    public async Task Filter_IgnoresNonPlaceholderItems()
    {
        var local = ((await Post(Local, Body(maxStreamingBitrate: 1_000))).MediaSources).ShouldHaveSingleItem();
        var other = ((await Post(OtherStrm, Body(maxStreamingBitrate: 1_000))).MediaSources).ShouldHaveSingleItem();

        local.Path.ShouldBe(LocalPath);
        local.SupportsDirectPlay.ShouldBeTrue();
        local.SupportsDirectStream.ShouldBeTrue();
        other.Path.ShouldBe(OtherHttp);
        other.SupportsDirectPlay.ShouldBeTrue();
        other.SupportsDirectStream.ShouldBeTrue();
    }

    [Fact]
    public async Task Filter_RunsOnGetAndPostPlaybackInfo()
    {
        var get = ((await Get<PlaybackInfoResponse>("/Items/" + Placeholder + "/PlaybackInfo")).MediaSources).ShouldHaveSingleItem();
        var post = ((await Post(Placeholder, null)).MediaSources).ShouldHaveSingleItem();

        get.Path.ShouldBe(string.Empty);
        get.SupportsDirectPlay.ShouldBeFalse();
        post.Path.ShouldBe(string.Empty);
        post.SupportsDirectPlay.ShouldBeFalse();
    }

    [Fact]
    public async Task Filter_ItemDto_HidesRelayPath()
    {
        var dto = await Get<BaseItemDto>("/Users/" + Guid.NewGuid() + "/Items/" + Placeholder);

        var source = dto.MediaSources.ShouldHaveSingleItem();
        source.Path.ShouldBe(string.Empty);
        source.SupportsDirectPlay.ShouldBeFalse();
    }

    [Fact]
    public async Task Filter_ItemQueryResult_HidesRelayPathOnly()
    {
        var result = await Get<QueryResult<BaseItemDto>>("/Items?Fields=MediaSources");
        var list = await Get<BaseItemDto[]>("/Items/Latest");

        result.Items.Select(i => i.MediaSources.Single().Path).ShouldBe(new[] { string.Empty, LocalPath, OtherHttp });
        list.Select(i => i.MediaSources.Single().Path).ShouldBe(new[] { string.Empty, LocalPath, OtherHttp });
    }

    [Fact]
    public async Task PlaybackInfo_LimitReached_Refuse_ReturnsRateLimitExceeded()
    {
        _sessions.Touch(OtherKey, TestItems.PeerA);

        var info = await Post(Placeholder, Body());

        info.ErrorCode.ShouldBe(PlaybackErrorCode.RateLimitExceeded);
        info.MediaSources.ShouldBeEmpty();
        _stopper.Calls.ShouldBeEmpty();
        _filterLog.Entries.ShouldContain(e => e.Level == LogLevel.Information && e.Message == "JellyShare: refused " + Key + ": peer alice allows 1 stream(s)");
    }

    [Fact]
    public async Task PlaybackInfo_LimitReached_StopOldest_StopsAndContinues()
    {
        _config.Settings.Peers[0].LimitAction = StreamLimitAction.StopOldest;
        _sessions.Touch(OtherKey, TestItems.PeerA);

        var info = await Post(Placeholder, Body());

        info.ErrorCode.ShouldBeNull();
        info.MediaSources.ShouldHaveSingleItem().Path.ShouldBe(string.Empty);
        _stopper.Calls.ShouldBe(new[] { (OtherKey, "Stopped: alice allows 1 stream(s) at a time and another viewer started.") });
        _sessions.ActiveTitles(TestItems.PeerA).ShouldBeEmpty();
    }

    [Fact]
    public async Task StopOldest_ReleasesSlotEvenIfStopFails()
    {
        _config.Settings.Peers[0].LimitAction = StreamLimitAction.StopOldest;
        _sessions.Touch(OtherKey, TestItems.PeerA);
        _stopper.Fail = true;

        var info = await Post(Placeholder, Body());

        info.ErrorCode.ShouldBeNull();
        info.MediaSources.ShouldHaveSingleItem();
        _stopper.Calls.ShouldHaveSingleItem();
        _sessions.ActiveTitles(TestItems.PeerA).ShouldBeEmpty();
    }

    [Fact]
    public async Task PlaybackInfo_TokenPeer_Unaffected()
    {
        _config.Settings.Peers[0].Kind = PeerKind.Token;
        _sessions.Touch(OtherKey, TestItems.PeerA);

        var info = await Post(Placeholder, Body());

        info.ErrorCode.ShouldBeNull();
        info.MediaSources.ShouldHaveSingleItem();
        _stopper.Calls.ShouldBeEmpty();
        _sessions.ActiveTitles(TestItems.PeerA).ShouldBe(new[] { OtherKey });
    }

    [Fact]
    public async Task PlaybackInfo_NotPlaceholder_Unaffected()
    {
        _config.Settings.Peers[0].LimitAction = StreamLimitAction.StopOldest;
        _sessions.Touch(OtherKey, TestItems.PeerA);

        var info = await Post(Local, Body());

        info.ErrorCode.ShouldBeNull();
        info.MediaSources.ShouldHaveSingleItem().Path.ShouldBe(LocalPath);
        _stopper.Calls.ShouldBeEmpty();
        _sessions.ActiveTitles(TestItems.PeerA).ShouldBe(new[] { OtherKey });
    }

    private sealed class FakeIndex : IConsumerIndex
    {
        public Dictionary<string, MergedEntry> Entries { get; } = new();

        public MergedEntry? Find(string canonicalKey) => Entries.GetValueOrDefault(canonicalKey);

        public IReadOnlyDictionary<Guid, PeerState> PeerStates() => new Dictionary<Guid, PeerState>();
    }

    private sealed class FakeStopper : IPlaybackStopper
    {
        public bool Fail { get; set; }

        public List<(string Key, string Message)> Calls { get; } = new();

        public Task<int> StopTitleAsync(string canonicalKey, string message, CancellationToken ct)
        {
            Calls.Add((canonicalKey, message));
            return Fail ? Task.FromException<int>(new InvalidOperationException("client gone")) : Task.FromResult(0);
        }
    }

    private sealed class FakePlaceholders : IPlaceholderLookup
    {
        public Dictionary<Guid, PlaceholderMedia> Items { get; } = new();

        public int? RemoteLimit { get; set; }

        public int? StoredProfileBitrate { get; set; }

        public PlaceholderMedia? Find(Guid itemId) => Items.GetValueOrDefault(itemId);

        public List<CallerContext> Callers { get; } = new();

        public Task<CallerLimits> GetCallerLimitsAsync(CallerContext caller)
        {
            Callers.Add(caller);
            return Task.FromResult(new CallerLimits(RemoteLimit, StoredProfileBitrate));
        }
    }

    private sealed class FakeAuthorization : IAuthorizationContext
    {
        public bool Fail { get; set; }

        public Task<AuthorizationInfo> GetAuthorizationInfo(HttpContext requestContext) => GetAuthorizationInfo(requestContext.Request);

        public Task<AuthorizationInfo> GetAuthorizationInfo(HttpRequest requestContext)
            => Fail
                ? Task.FromException<AuthorizationInfo>(new InvalidOperationException("no auth"))
                : Task.FromResult(new AuthorizationInfo { DeviceId = "device-1" });
    }
}

/// <summary>Stand-in for Jellyfin's PlaybackInfoDto (lives in Jellyfin.Api, not available to plugins).</summary>
public sealed class FakePlaybackInfoDto
{
    public int? MaxStreamingBitrate { get; set; }

    public DeviceProfile? DeviceProfile { get; set; }

    public bool? EnableDirectPlay { get; set; }

    public bool? EnableDirectStream { get; set; }
}

/// <summary>Same routes and parameter names as Jellyfin 12.4's MediaInfoController / item endpoints.</summary>
[Route("")]
[SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "MVC actions must be instance methods.")]
public sealed class FakeMediaInfoController : ControllerBase
{
    private static readonly Dictionary<Guid, string> _paths = new()
    {
        [PlaybackInfoFilterTests.Placeholder] = PlaybackInfoFilterTests.RelayUrl,
        [PlaybackInfoFilterTests.Local] = PlaybackInfoFilterTests.LocalPath,
        [PlaybackInfoFilterTests.OtherStrm] = PlaybackInfoFilterTests.OtherHttp,
    };

    [HttpGet("Items/{itemId}/PlaybackInfo")]
    public ActionResult<PlaybackInfoResponse> GetPlaybackInfo([FromRoute] Guid itemId, [FromQuery] Guid? userId)
        => new PlaybackInfoResponse { MediaSources = new[] { Source(itemId, true, true, null) } };

    [HttpPost("Items/{itemId}/PlaybackInfo")]
    public ActionResult<PlaybackInfoResponse> GetPostedPlaybackInfo(
        [FromRoute] Guid itemId,
        [FromQuery] Guid? userId,
        [FromQuery] int? maxStreamingBitrate,
        [FromQuery] bool? enableDirectPlay,
        [FromQuery] bool? enableDirectStream,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] FakePlaybackInfoDto? playbackInfoDto)
    {
        maxStreamingBitrate ??= playbackInfoDto?.MaxStreamingBitrate ?? playbackInfoDto?.DeviceProfile?.MaxStreamingBitrate;
        enableDirectPlay ??= playbackInfoDto?.EnableDirectPlay ?? true;
        enableDirectStream ??= playbackInfoDto?.EnableDirectStream ?? true;
        var transcodingUrl = !enableDirectPlay.Value && !enableDirectStream.Value
            ? "/transcode?bitrate=" + maxStreamingBitrate
            : null;
        return new PlaybackInfoResponse
        {
            MediaSources = new[] { Source(itemId, enableDirectPlay.Value, enableDirectStream.Value, transcodingUrl) },
        };
    }

    [HttpGet("Users/{userId}/Items/{itemId}")]
    public ActionResult<BaseItemDto> GetItem([FromRoute] Guid userId, [FromRoute] Guid itemId)
        => Dto(itemId);

    [HttpGet("Items")]
    public ActionResult<QueryResult<BaseItemDto>> GetItems()
        => new QueryResult<BaseItemDto>(_paths.Keys.Order().Select(Dto).ToList());

    public static int LazyEnumerations { get; set; }

    // Like Jellyfin 12.4 UserLibraryController.GetSpecialFeatures / GetLocalTrailers: a lazy Select.
    [HttpGet("Users/{userId}/Items/{itemId}/SpecialFeatures")]
    public ActionResult<IEnumerable<BaseItemDto>> GetSpecialFeatures([FromRoute] Guid userId, [FromRoute] Guid itemId)
        => Ok(Lazy());

    [HttpGet("Items/Latest")]
    public ActionResult<IEnumerable<BaseItemDto>> GetLatest()
        => _paths.Keys.Order().Select(Dto).ToList();

    private static IEnumerable<BaseItemDto> Lazy()
    {
        LazyEnumerations++;
        foreach (var id in _paths.Keys.Order())
        {
            yield return Dto(id);
        }
    }

    private static BaseItemDto Dto(Guid itemId)
        => new() { Id = itemId, MediaSources = new[] { Source(itemId, true, true, null) } };

    private static MediaSourceInfo Source(Guid itemId, bool directPlay, bool directStream, string? transcodingUrl)
        => new()
        {
            Id = itemId.ToString("N"),
            Path = _paths[itemId],
            Protocol = _paths[itemId].StartsWith("http", StringComparison.Ordinal) ? MediaProtocol.Http : MediaProtocol.File,
            SupportsDirectPlay = directPlay,
            SupportsDirectStream = directStream,
            SupportsTranscoding = true,
            TranscodingUrl = transcodingUrl,
        };
}
