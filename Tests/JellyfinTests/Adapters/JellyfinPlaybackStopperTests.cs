using System.Reflection;
using JellyShare.Jellyfin.Adapters;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;

namespace JellyShare.JellyfinTests.Adapters;

public sealed class JellyfinPlaybackStopperTests
{
    private const string Key = "movie:tmdb:10378";

    private static readonly Guid _placeholder = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid _otherPlaceholder = Guid.Parse("11111111-0000-0000-0000-000000000002");
    private static readonly Guid _trackOne = Guid.Parse("11111111-0000-0000-0000-000000000011");
    private static readonly Guid _trackTwo = Guid.Parse("11111111-0000-0000-0000-000000000012");
    private static readonly Guid _local = Guid.Parse("22222222-0000-0000-0000-000000000002");

    private static SessionInfo Session(ISessionManager manager, string id, Guid? playing)
        => new(manager, NullLogger.Instance)
        {
            Id = id,
            NowPlayingItem = playing is { } item ? new BaseItemDto { Id = item } : null,
        };

    [Fact]
    public async Task StopTitle_OnlySessionsPlayingThatTitle()
    {
        var manager = SessionManagerProxy.Create();
        var proxy = (SessionManagerProxy)(object)manager;
        proxy.Sessions.AddRange(new[]
        {
            Session(manager, "s1", _placeholder),
            Session(manager, "s2", _otherPlaceholder),
            Session(manager, "s3", _local),
            Session(manager, "s4", null),
            Session(manager, "s5", _placeholder),
        });
        proxy.FailFor.Add("s5");
        var lookup = new Lookup();
        var stopper = new JellyfinPlaybackStopper(manager, lookup, NullLogger<JellyfinPlaybackStopper>.Instance);

        var stopped = await stopper.StopTitleAsync(Key, "Stopped: alice", CancellationToken.None);

        stopped.ShouldBe(1);
        proxy.Calls.ShouldBe(new[]
        {
            "message:(null)->s1:JellyShare:Stopped: alice:10000",
            "stop:(null)->s1:Stop",
            "message:(null)->s5:JellyShare:Stopped: alice:10000",
            "stop:(null)->s5:Stop",
        });
    }

    [Fact]
    public async Task StopTitle_Album_StopsEveryTrackOfIt()
    {
        var manager = SessionManagerProxy.Create();
        var proxy = (SessionManagerProxy)(object)manager;
        proxy.Sessions.AddRange(new[]
        {
            Session(manager, "s1", _trackOne),
            Session(manager, "s2", _trackTwo),
            Session(manager, "s3", _otherPlaceholder),
        });
        var stopper = new JellyfinPlaybackStopper(manager, new Lookup(), NullLogger<JellyfinPlaybackStopper>.Instance);

        var stopped = await stopper.StopTitleAsync("album:name:abc", "Stopped", CancellationToken.None);

        stopped.ShouldBe(2);
        proxy.Calls.Where(c => c.StartsWith("stop:", StringComparison.Ordinal)).ShouldBe(new[] { "stop:(null)->s1:Stop", "stop:(null)->s2:Stop" });
    }

    private sealed class Lookup : IPlaceholderLookup
    {
        private readonly Dictionary<Guid, PlaceholderMedia> _items = new()
        {
            [_placeholder] = new PlaceholderMedia(Key, null),
            [_otherPlaceholder] = new PlaceholderMedia("movie:tmdb:2", null),
            [_trackOne] = new PlaceholderMedia("track:name:1", null, "album:name:abc"),
            [_trackTwo] = new PlaceholderMedia("track:name:2", null, "album:name:abc"),
        };

        public PlaceholderMedia? Find(Guid itemId) => _items.GetValueOrDefault(itemId);

        public Task<CallerLimits> GetCallerLimitsAsync(CallerContext caller) => throw new NotSupportedException();
    }

    /// <summary>A stand-in for Jellyfin's large ISessionManager: only what the stopper uses.</summary>
    public class SessionManagerProxy : DispatchProxy
    {
        public List<SessionInfo> Sessions { get; } = new();

        public List<string> Calls { get; } = new();

        public HashSet<string> FailFor { get; } = new();

        public static ISessionManager Create() => Create<ISessionManager, SessionManagerProxy>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "get_Sessions":
                    return Sessions;
                case nameof(ISessionManager.SendMessageCommand):
                    var message = (MessageCommand)args![2]!;
                    Calls.Add($"message:{args[0] ?? "(null)"}->{args[1]}:{message.Header}:{message.Text}:{message.TimeoutMs}");
                    return Task.CompletedTask;
                case nameof(ISessionManager.SendPlaystateCommand):
                    var request = (PlaystateRequest)args![2]!;
                    Calls.Add($"stop:{args[0] ?? "(null)"}->{args[1]}:{request.Command}");
                    return FailFor.Contains((string)args[1]!) ? Task.FromException(new InvalidOperationException("client gone")) : Task.CompletedTask;
                default:
                    throw new NotSupportedException(targetMethod?.Name);
            }
        }
    }
}
