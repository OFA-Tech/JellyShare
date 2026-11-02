using System.Net;
using System.Net.Http.Headers;
using JellyShare.Data.Peers.Clients;
using JellyShare.Application.Features.Relay;
using JellyShare.Domain.Enums;
using JellyShare.Domain.Interfaces.Sync;
using JellyShare.Domain.Keys;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Protocol;
using JellyShare.Domain.Models.Sync;
using JellyShare.Jellyfin.Controllers.V1;
using JellyShare.JellyfinTests.Fakes;
using JellyShare.UnitTests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Logging;

namespace JellyShare.JellyfinTests.Controllers;

public sealed class RelayControllerTests
{
    private const string Key = "movie:tmdb:10378";
    private const string TokenA = "js1_ALICE-secret-token";
    private const string TokenB = "js1_BOB-secret-token";

    private static readonly Guid _a = TestItems.PeerA;
    private static readonly Guid _b = TestItems.PeerB;

    private readonly ConsumerConfig _config = new();
    private readonly FakeConsumerIndex _index = new();
    private readonly Dictionary<string, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _hosts = new();
    private readonly List<HttpRequestMessage> _upstream = new();
    private readonly ListLogger _logger = new();
    private readonly MutableClock _clock = new();
    private readonly RelaySessions _sessions;

    public RelayControllerTests()
    {
        _sessions = new RelaySessions(_clock);
        _config.Settings.Peers.Add(new RemotePeer { Id = _a, Name = "alice", BaseUrl = "http://alice:8101", Token = TokenA, Priority = 0 });
        _config.Settings.Peers.Add(new RemotePeer { Id = _b, Name = "bob", BaseUrl = "http://bob/jellyfin", Token = TokenB, Priority = 1 });
        _index.Entries[Key] = new MergedEntry(
            Key,
            new[] { Key },
            new List<(Guid, CatalogItem)>
            {
                (_b, TestItems.Movie("BBB", tmdb: "10378", id: "bob-item")),
                (_a, TestItems.Movie("BBB", tmdb: "10378", id: "alice-item")),
            });
    }

    private static HttpResponseMessage Video(byte[] body, HttpStatusCode code = HttpStatusCode.OK, string? contentRange = null)
    {
        var response = new HttpResponseMessage(code) { Content = new ByteArrayContent(body) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("video/x-matroska");
        response.Content.Headers.ContentLength = body.Length;
        if (contentRange is not null)
        {
            response.Content.Headers.TryAddWithoutValidation("Content-Range", contentRange);
        }

        response.Headers.AcceptRanges.Add("bytes");
        return response;
    }

    private void Host(string host, Func<HttpRequestMessage, HttpResponseMessage> respond)
        => _hosts[host] = (r, _) => Task.FromResult(respond(r));

    private RelayController CreateController(DefaultHttpContext context, TimeSpan? headerTimeout = null, TimeSpan? idleTimeout = null)
    {
        var handler = new FakeHandler((request, ct) =>
        {
            lock (_upstream)
            {
                _upstream.Add(request);
            }

            return _hosts.TryGetValue(request.RequestUri!.Host, out var respond)
                ? respond(request, ct)
                : throw new HttpRequestException("connection refused");
        });
        var client = new PeerClient(() => new HttpClient(handler, false));
        return new RelayController(_config, _index, client, _sessions, _logger, headerTimeout ?? TimeSpan.FromSeconds(5), idleTimeout ?? TimeSpan.FromSeconds(5))
        {
            ControllerContext = new ControllerContext { HttpContext = context },
        };
    }

    private static DefaultHttpContext Context(string method = "GET", IPAddress? remote = null, string? range = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Connection.RemoteIpAddress = remote ?? IPAddress.Loopback;
        context.Response.Body = new MemoryStream();
        if (range is not null)
        {
            context.Request.Headers.Range = range;
        }

        // What Jellyfin's ffmpeg sends; must not matter.
        context.Request.Headers["Icy-MetaData"] = "1";
        context.Request.Headers.Connection = "close";
        context.Request.Headers.UserAgent = "Lavf/62.12.103";
        return context;
    }

    private static int StatusOf(IActionResult result, HttpContext context)
        => result is IStatusCodeActionResult { StatusCode: { } code } ? code : context.Response.StatusCode;

    private static byte[] Body(HttpContext context) => ((MemoryStream)context.Response.Body).ToArray();

    [Theory]
    [InlineData("192.168.1.20")]
    [InlineData("172.17.0.1")]
    [InlineData("10.0.0.5")]
    [InlineData("::ffff:192.168.1.20")]
    public async Task Relay_NonLoopback_Returns403(string ip)
    {
        Host("bob", _ => Video(new byte[] { 1 }));
        var context = Context(remote: IPAddress.Parse(ip));

        var result = await CreateController(context).Play(Key);

        StatusOf(result, context).ShouldBe(403);
        _upstream.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("X-Forwarded-For", "203.0.113.9")]
    [InlineData("X-Forwarded-Host", "example.org")]
    [InlineData("X-Real-IP", "203.0.113.9")]
    [InlineData("Forwarded", "for=203.0.113.9")]
    [InlineData("X-Original-For", "203.0.113.9:1234")]
    [InlineData("X-Original-Host", "example.org")]
    [InlineData("X-Original-Proto", "https")]
    [InlineData("X-Forwarded-Proto", "https")]
    [InlineData("X-Forwarded-Port", "443")]
    [InlineData("Via", "1.1 proxy")]
    [InlineData("CF-Connecting-IP", "203.0.113.9")]
    [InlineData("True-Client-IP", "203.0.113.9")]
    [InlineData("x-forwarded-for", "203.0.113.9")]
    public async Task Relay_LoopbackWithForwardedHeader_Returns403(string header, string value)
    {
        Host("bob", _ => Video(new byte[] { 1 }));
        var context = Context();
        context.Request.Headers[header] = value;

        var result = await CreateController(context).Play(Key);

        StatusOf(result, context).ShouldBe(403);
        _upstream.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public async Task Relay_Loopback_IsAllowed(string ip)
    {
        Host("alice", _ => Video(new byte[] { 1, 2, 3 }));
        var context = Context(remote: IPAddress.Parse(ip));

        var result = await CreateController(context).Play(Key);

        StatusOf(result, context).ShouldBe(200);
        Body(context).ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Fact]
    public async Task Relay_UnknownKey_Returns404()
    {
        var context = Context();

        var result = await CreateController(context).Play("movie:tmdb:999");

        StatusOf(result, context).ShouldBe(404);
        _upstream.ShouldBeEmpty();
    }

    [Fact]
    public async Task Relay_OpenEndedRange_ForwardsRangeAndCopiesHeaders()
    {
        var body = Enumerable.Range(0, 96).Select(i => (byte)i).ToArray();
        Host("alice", _ => Video(body, HttpStatusCode.PartialContent, "bytes 4000-4095/4096"));
        var context = Context(range: "bytes=4000-");

        var result = await CreateController(context).Play(Key);

        StatusOf(result, context).ShouldBe(206);
        context.Response.ContentType.ShouldBe("video/x-matroska");
        context.Response.ContentLength.ShouldBe(96);
        context.Response.Headers.ContentRange.ToString().ShouldBe("bytes 4000-4095/4096");
        context.Response.Headers.AcceptRanges.ToString().ShouldBe("bytes");
        Body(context).ShouldBe(body);

        var sent = _upstream.ShouldHaveSingleItem();
        sent.RequestUri!.ToString().ShouldBe("http://alice:8101/JellyShare/v1/stream/alice-item");
        string.Join(",", sent.Headers.GetValues("Range")).ShouldBe("bytes=4000-");
        string.Join(",", sent.Headers.GetValues("X-JellyShare-Token")).ShouldBe(TokenA);
        sent.Headers.Contains("Icy-MetaData").ShouldBeFalse();
    }

    [Fact]
    public async Task Relay_ClosedRange_IsForwarded()
    {
        Host("alice", _ => Video(new byte[1024], HttpStatusCode.PartialContent, "bytes 0-1023/19691603"));
        var context = Context(range: "bytes=0-1023");

        var result = await CreateController(context).Play(Key);

        StatusOf(result, context).ShouldBe(206);
        string.Join(",", _upstream.ShouldHaveSingleItem().Headers.GetValues("Range")).ShouldBe("bytes=0-1023");
        Body(context).Length.ShouldBe(1024);
    }

    [Fact]
    public async Task Relay_NoRange_SendsNoRange()
    {
        Host("alice", _ => Video(new byte[10]));
        var context = Context();

        var result = await CreateController(context).Play(Key);

        StatusOf(result, context).ShouldBe(200);
        _upstream.ShouldHaveSingleItem().Headers.Contains("Range").ShouldBeFalse();
        Body(context).Length.ShouldBe(10);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(409)]
    [InlineData(503)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(302)]
    [InlineData(307)]
    [InlineData(401)]
    [InlineData(404)]
    public async Task Relay_UpstreamError_FailsOverToNextPeer(int code)
    {
        Host("alice", _ =>
        {
            var r = new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent("{\"error\":\"x\"}") };
            if (code is >= 300 and < 400)
            {
                r.Headers.Location = new Uri("http://evil.example/steal");
            }

            return r;
        });
        Host("bob", _ => Video(new byte[] { 9, 9 }));
        var context = Context(range: "bytes=0-");

        var result = await CreateController(context).Play(Key);

        StatusOf(result, context).ShouldBe(200);
        Body(context).ShouldBe(new byte[] { 9, 9 });
        _upstream.Select(r => r.RequestUri!.Host).ShouldBe(new[] { "alice", "bob" });
        _upstream.ShouldNotContain(r => r.RequestUri!.Host == "evil.example");
        string.Join(",", _upstream[1].Headers.GetValues("Range")).ShouldBe("bytes=0-");
        _logger.Lines.ShouldNotContain(l => l.Contains(TokenA, StringComparison.Ordinal) || l.Contains(TokenB, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Relay_Timeout_FailsOverToNextPeer()
    {
        _hosts["alice"] = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        };
        Host("bob", _ => Video(new byte[] { 7 }));
        var context = Context();

        var result = await CreateController(context, TimeSpan.FromMilliseconds(200)).Play(Key);

        StatusOf(result, context).ShouldBe(200);
        Body(context).ShouldBe(new byte[] { 7 });
    }

    [Fact]
    public async Task Relay_ConnectionRefused_FailsOverToNextPeer()
    {
        _hosts["alice"] = (_, _) => throw new HttpRequestException("Connection refused " + TokenA);
        Host("bob", _ => Video(new byte[] { 5 }));
        var context = Context();

        var result = await CreateController(context).Play(Key);

        StatusOf(result, context).ShouldBe(200);
        _logger.Lines.ShouldNotContain(l => l.Contains(TokenA, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Relay_AllPeersFail_Returns503()
    {
        Host("alice", _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        Host("bob", _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var context = Context();

        var result = await CreateController(context).Play(Key);

        StatusOf(result, context).ShouldBe(503);
        _upstream.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Relay_NoUsablePeer_Returns503()
    {
        foreach (var peer in _config.Current.Peers)
        {
            peer.Enabled = false;
        }

        var context = Context();

        var result = await CreateController(context).Play(Key);

        StatusOf(result, context).ShouldBe(503);
        _upstream.ShouldBeEmpty();
    }

    [Fact]
    public async Task Relay_FailingPeerIsTriedLast()
    {
        _index.States[_a] = new PeerState { FailingSinceUtc = DateTimeOffset.UnixEpoch };
        Host("alice", _ => Video(new byte[] { 1 }));
        Host("bob", _ => Video(new byte[] { 2 }));
        var context = Context();

        await CreateController(context).Play(Key);

        Body(context).ShouldBe(new byte[] { 2 });
        _upstream.ShouldHaveSingleItem().RequestUri!.Host.ShouldBe("bob");
    }

    [Fact]
    public async Task Relay_PeerThatFailedLastSync_IsStillUsedWhenOthersFail()
    {
        _index.States[_a] = new PeerState { FailingSinceUtc = DateTimeOffset.UnixEpoch };
        Host("alice", _ => Video(new byte[] { 1 }));
        Host("bob", _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var context = Context();

        await CreateController(context).Play(Key);

        Body(context).ShouldBe(new byte[] { 1 });
        _upstream.Select(r => r.RequestUri!.Host).ShouldBe(new[] { "bob", "alice" });
    }

    [Fact]
    public async Task Relay_Head_CopiesHeadersWithoutBody()
    {
        Host("alice", _ => Video(new byte[4096]));
        var context = Context("HEAD");

        var result = await CreateController(context).Play(Key);

        StatusOf(result, context).ShouldBe(200);
        context.Response.ContentLength.ShouldBe(4096);
        Body(context).ShouldBeEmpty();
    }

    [Fact]
    public async Task Relay_FailureAfterFirstByte_DoesNotTryNextPeer()
    {
        Host("alice", _ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FailingStream(new byte[] { 1, 2, 3 })) };
            r.Content.Headers.ContentLength = 100;
            return r;
        });
        Host("bob", _ => Video(new byte[] { 9 }));
        var context = Context();

        var result = await CreateController(context).Play(Key);

        StatusOf(result, context).ShouldBe(200);
        Body(context).ShouldBe(new byte[] { 1, 2, 3 });
        _upstream.ShouldHaveSingleItem().RequestUri!.Host.ShouldBe("alice");
    }

    [Fact]
    public async Task Relay_ClientDisconnect_CancelsUpstream()
    {
        var upstreamBody = new BlockingStream();
        Host("alice", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(upstreamBody) });
        using var abort = new CancellationTokenSource();
        var context = Context();
        context.RequestAborted = abort.Token;

        var play = CreateController(context).Play(Key);
        await upstreamBody.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await abort.CancelAsync();
        await play.WaitAsync(TimeSpan.FromSeconds(5));

        upstreamBody.ReadCancelled.ShouldBeTrue();
    }

    [Fact]
    public async Task Relay_StalledPeerAfterCommit_AbortsResponse()
    {
        var upstreamBody = new BlockingStream(new byte[] { 4, 5, 6 });
        Host("alice", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(upstreamBody) });
        Host("bob", _ => Video(new byte[] { 9 }));
        var context = Context();
        var lifetime = new RecordingLifetime();
        context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);

        await CreateController(context, idleTimeout: TimeSpan.FromMilliseconds(300)).Play(Key).WaitAsync(TimeSpan.FromSeconds(5));

        Body(context).ShouldBe(new byte[] { 4, 5, 6 });
        lifetime.Aborted.ShouldBeTrue();
        upstreamBody.ReadCancelled.ShouldBeTrue();
        _upstream.ShouldHaveSingleItem().RequestUri!.Host.ShouldBe("alice");
        _logger.Lines.ShouldContain(l => l.Contains("stalled", StringComparison.Ordinal) && l.Contains("alice", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Relay_ClientGoneDuringWrite_IsNotLoggedAsPeerFailure()
    {
        Host("alice", _ => Video(new byte[100]));
        using var abort = new CancellationTokenSource();
        var context = Context();
        context.RequestAborted = abort.Token;
        context.Response.Body = new DisconnectingStream(abort);

        await CreateController(context).Play(Key).WaitAsync(TimeSpan.FromSeconds(5));

        _logger.Lines.ShouldNotContain(l => l.Contains("broke", StringComparison.Ordinal) || l.Contains("stalled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Relay_SamePlayback_StaysOnServingPeer()
    {
        var aliceCalls = 0;
        Host("alice", _ =>
        {
            aliceCalls++;
            return aliceCalls == 1 ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) : Video(new byte[] { 1 });
        });
        Host("bob", _ => Video(new byte[] { 2 }));

        var first = Context();
        await CreateController(first).Play(Key);
        _clock.Now = _clock.Now.AddSeconds(10);
        var second = Context(range: "bytes=500-");
        await CreateController(second).Play(Key);

        Body(first).ShouldBe(new byte[] { 2 });
        Body(second).ShouldBe(new byte[] { 2 });
        aliceCalls.ShouldBe(1);
        _logger.Lines.Where(l => l.Contains("bob", StringComparison.Ordinal) && l.StartsWith("Information", StringComparison.Ordinal)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Relay_AfterGrace_NewPlaybackUsesPreferredPeerAgain()
    {
        var aliceCalls = 0;
        Host("alice", _ =>
        {
            aliceCalls++;
            return aliceCalls == 1 ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) : Video(new byte[] { 1 });
        });
        Host("bob", _ => Video(new byte[] { 2 }));

        await CreateController(Context()).Play(Key);
        _clock.Now = _clock.Now.Add(RelaySessions.Grace).AddSeconds(1);
        var later = Context();
        await CreateController(later).Play(Key);

        Body(later).ShouldBe(new byte[] { 1 });
    }

    [Fact]
    public async Task Play_AccountPeerFull_SkipsToNextSource()
    {
        _config.Current.Peers[0].Kind = PeerKind.Account;
        _sessions.Touch("movie:tmdb:other", _a);
        Host("alice", _ => Video(new byte[] { 1 }));
        Host("bob", _ => Video(new byte[] { 2 }));
        var context = Context();

        var result = await CreateController(context).Play(Key);

        StatusOf(result, context).ShouldBe(200);
        Body(context).ShouldBe(new byte[] { 2 });
        _upstream.ShouldHaveSingleItem().RequestUri!.Host.ShouldBe("bob");
    }

    [Fact]
    public async Task Play_AccountPeerFull_SameTitleSeek_IsServed()
    {
        _config.Current.Peers[0].Kind = PeerKind.Account;
        _sessions.Touch(Key, _a);
        Host("alice", _ => Video(new byte[] { 1 }, HttpStatusCode.PartialContent, "bytes 5-5/6"));
        Host("bob", _ => Video(new byte[] { 2 }));
        var context = Context(range: "bytes=5-");

        var result = await CreateController(context).Play(Key);

        StatusOf(result, context).ShouldBe(206);
        _upstream.ShouldHaveSingleItem().RequestUri!.Host.ShouldBe("alice");
    }

    [Fact]
    public async Task Play_AllFull_Is503StreamLimit()
    {
        foreach (var peer in _config.Current.Peers)
        {
            peer.Kind = PeerKind.Account;
            _sessions.Touch("movie:tmdb:" + peer.Name, peer.Id);
        }

        var context = Context();

        var result = await CreateController(context).Play(Key);

        StatusOf(result, context).ShouldBe(503);
        ((ObjectResult)result).Value.ShouldBeOfType<ErrorResponse>().Error.ShouldBe("stream limit reached");
        _upstream.ShouldBeEmpty();
        _logger.Lines.Where(l => l.StartsWith("Information", StringComparison.Ordinal) && l.Contains("alice", StringComparison.Ordinal) && l.Contains("bob", StringComparison.Ordinal))
            .ShouldHaveSingleItem();
        _logger.Lines.ShouldNotContain(l => l.Contains(TokenA, StringComparison.Ordinal) || l.Contains(TokenB, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Play_FullAccountPeerAndFailingPeer_Is503NoPeerAvailable()
    {
        _config.Current.Peers[0].Kind = PeerKind.Account;
        _sessions.Touch("movie:tmdb:other", _a);
        Host("bob", _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var context = Context();

        var result = await CreateController(context).Play(Key);

        ((ObjectResult)result).Value.ShouldBeOfType<ErrorResponse>().Error.ShouldBe("no peer available");
    }

    private sealed class FakeConsumerIndex : IConsumerIndex
    {
        public Dictionary<string, MergedEntry> Entries { get; } = new();

        public Dictionary<Guid, PeerState> States { get; } = new();

        public MergedEntry? Find(string canonicalKey) => Entries.GetValueOrDefault(canonicalKey);

        public IReadOnlyDictionary<Guid, PeerState> PeerStates() => States;
    }

    private sealed class FailingStream : Stream
    {
        private readonly byte[] _first;
        private bool _sent;

        public FailingStream(byte[] first) => _first = first;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_sent)
            {
                throw new IOException("peer connection reset");
            }

            _sent = true;
            Array.Copy(_first, 0, buffer, offset, _first.Length);
            return _first.Length;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class RecordingLifetime : IHttpRequestLifetimeFeature
    {
        public CancellationToken RequestAborted { get; set; }

        public bool Aborted { get; private set; }

        public void Abort() => Aborted = true;
    }

    private sealed class DisconnectingStream : Stream
    {
        private readonly CancellationTokenSource _abort;

        public DisconnectingStream(CancellationTokenSource abort) => _abort = abort;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count)
        {
            // Kestrel: the client went away; RequestAborted fires and the write fails with an IOException.
            _abort.Cancel();
            throw new IOException("client disconnected");
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(Array.Empty<byte>(), 0, 0);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class BlockingStream : Stream
    {
        private byte[]? _prefix;

        public BlockingStream(byte[]? prefix = null) => _prefix = prefix;

        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ReadCancelled { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_prefix is { } prefix)
            {
                _prefix = null;
                prefix.CopyTo(buffer);
                return prefix.Length;
            }

            ReadStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                ReadCancelled = true;
                throw;
            }

            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

internal sealed class ListLogger : ILogger<RelayController>
{
    public List<string> Lines { get; } = new();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Lines)
        {
            Lines.Add(logLevel + ": " + formatter(state, exception) + (exception is null ? string.Empty : " | " + exception));
        }
    }
}
