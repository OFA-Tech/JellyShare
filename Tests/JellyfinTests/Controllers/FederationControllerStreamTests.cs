using System.Reflection;
using JellyShare.Application.Features.Catalog;
using JellyShare.Application.Features.Streaming;
using JellyShare.Domain.Enums;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Library;
using JellyShare.Jellyfin.Controllers.V1;
using JellyShare.JellyfinTests.Fakes;
using JellyShare.JellyfinTests.Fakes;
using JellyShare.UnitTests.Fakes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JellyShare.JellyfinTests.Controllers;

public sealed class FederationControllerStreamTests : IDisposable
{
    private static readonly Guid Lib = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid MovieId = Guid.Parse("00000001-0000-0000-0000-000000000001");
    private static readonly Guid Movie2Id = Guid.Parse("00000001-0000-0000-0000-000000000002");
    private static readonly Guid EpisodeId = Guid.Parse("00000002-0000-0000-0000-000000000001");
    private static readonly Guid SeriesId = Guid.Parse("00000003-0000-0000-0000-000000000001");
    private static readonly Guid OutsideId = Guid.Parse("00000004-0000-0000-0000-000000000001");

    private readonly FakeSource _source = new();
    private readonly FakeConfig _config = new();
    private readonly ShareTokenService _tokens = new();
    private readonly ManualTimeProvider _clock = new();
    private readonly StreamLimiter _limiter;
    private readonly ListLogger _logger = new();
    private readonly FakeEncoderSettings _encoder = new();
    private readonly FakeFfmpegRunner _runner = new();
    private readonly List<CompletableResponseFeature> _responses = new();
    private readonly LevelLogger<CappedStreamService> _cappedLogger = new();
    private CappedStreamService? _capped;
    private readonly CancellationTokenSource _abort = new();
    private readonly List<RecordingLifetime> _lifetimes = new();
    private readonly string _token;
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "jellyshare-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _movieFile;
    private readonly string _movie2File;

    public FederationControllerStreamTests()
    {
        Directory.CreateDirectory(_tempDir);
        _limiter = new StreamLimiter(_clock);
        var (token, hash) = _tokens.Create();
        _token = token;
        _config.Settings.Shares.Add(new PeerShare
        {
            Id = Guid.NewGuid(),
            Name = "bob",
            TokenHash = hash,
            Enabled = true,
            LibraryIds = new List<Guid> { Lib },
            MaxStreams = 1,
        });

        _movieFile = Path.Combine(_tempDir, "a.mkv");
        _movie2File = Path.Combine(_tempDir, "b.mp4");
        File.WriteAllBytes(_movieFile, new byte[4096]);
        File.WriteAllBytes(_movie2File, new byte[4096]);
        AddItem(Lib, MovieId, "Movie", _movieFile, null);
        AddItem(Lib, Movie2Id, "Movie", _movie2File, null);
        AddItem(Lib, EpisodeId, "Episode", Path.Combine(_tempDir, "e.mkv"), SeriesId);
        File.WriteAllBytes(Path.Combine(_tempDir, "e.mkv"), new byte[10]);
        AddItem(Guid.NewGuid(), OutsideId, "Movie", _movieFile, null);
    }

    public void Dispose()
    {
        _abort.Cancel();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private void AddItem(Guid library, Guid id, string type, string path, Guid? seriesId)
    {
        var item = new LocalItem(id, type, path, seriesId, () => throw new InvalidOperationException("catalog entry not needed"));
        _source.Items.Add((library, item));
        _source.Streams[id] = new StreamInfo(type, "Name of " + id.ToString("N")[..8], path, 1_000_000);
    }

    private FederationController Controller(string? token, string method = "GET", CancellationToken aborted = default)
    {
        var builder = new CatalogBuilder(_source, new FakeIdentity(), _config, _ => true);
        _capped ??= new CappedStreamService(_encoder, _runner, _clock, _cappedLogger);
        var controller = new FederationController(FederationSender.Create(new FakeIdentity(), builder, _config, _tokens), builder, _source, _config, _tokens, _limiter, _capped, _logger);
        var http = new DefaultHttpContext();
        var response = new CompletableResponseFeature();
        http.Features.Set<IHttpResponseFeature>(response);
        _responses.Add(response);
        http.Request.Method = method;
        http.Response.Body = new MemoryStream();
        var lifetime = new RecordingLifetime(_abort.Token, aborted);
        http.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        _lifetimes.Add(lifetime);
        if (token is not null)
        {
            http.Request.Headers["X-JellyShare-Token"] = token;
        }

        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private void CompleteAllResponses()
    {
        foreach (var response in _responses)
        {
            response.CompleteAsync().GetAwaiter().GetResult();
        }
    }

    private void CapMovie(int cap = 2_000_000, long bitrate = 8_000_000)
    {
        _config.Settings.Shares[0].MaxBitrate = cap;
        _source.Streams[MovieId] = _source.Streams[MovieId] with { Bitrate = bitrate, RunTimeTicks = TimeSpan.FromMinutes(90).Ticks };
    }

    private static async Task<HttpContext> Execute(FederationController controller, IActionResult result)
    {
        await result.ExecuteResultAsync(new ActionContext(controller.HttpContext, new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()));
        return controller.HttpContext;
    }

    private static byte[] Body(HttpContext http) => ((MemoryStream)http.Response.Body).ToArray();

    [Fact]
    public void Stream_NoToken_Returns401()
    {
        var result = Controller(null).Stream(MovieId.ToString("N"));

        Assert.Equal(401, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public void Stream_WrongToken_Returns401_AndDoesNotEchoToken()
    {
        const string wrong = "js1_not-a-real-token-value";

        var result = Assert.IsAssignableFrom<ObjectResult>(Controller(wrong).Stream(MovieId.ToString("N")));

        Assert.Equal(401, result.StatusCode);
        Assert.DoesNotContain(wrong, System.Text.Json.JsonSerializer.Serialize(result.Value));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("../../etc/passwd")]
    public void Stream_BadItemId_Returns404(string id)
    {
        Assert.IsType<NotFoundResult>(Controller(_token).Stream(id));
    }

    [Fact]
    public void Stream_UnknownItem_Returns404()
    {
        Assert.IsType<NotFoundResult>(Controller(_token).Stream(Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void Stream_ItemOutsideShare_Returns404()
    {
        Assert.IsType<NotFoundResult>(Controller(_token).Stream(OutsideId.ToString("N")));
    }

    [Fact]
    public void Stream_SeriesId_Returns404_EvenThoughImagesAreExposed()
    {
        Assert.IsType<NotFoundResult>(Controller(_token).Stream(SeriesId.ToString("N")));
    }

    [Fact]
    public void Stream_Movie_ServesFileWithRangeProcessing()
    {
        var result = Controller(_token).Stream(MovieId.ToString("N"));

        var file = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(_movieFile, file.FileName);
        Assert.Equal("video/x-matroska", file.ContentType);
        Assert.True(file.EnableRangeProcessing);
    }

    [Fact]
    public void Stream_Episode_IsServed()
    {
        Assert.IsType<PhysicalFileResult>(Controller(_token).Stream(EpisodeId.ToString("N")));
    }

    [Theory]
    [InlineData("movie.mkv", "video/x-matroska")]
    [InlineData("movie.mp4", "video/mp4")]
    [InlineData("movie.webm", "video/webm")]
    [InlineData("movie.avi", "video/x-msvideo")]
    [InlineData("movie.xyzunknown", "application/octet-stream")]
    public void Stream_ContentTypeFromExtension(string name, string contentType)
    {
        var file = Path.Combine(_tempDir, name);
        File.WriteAllBytes(file, new byte[8]);
        AddItem(Lib, Guid.Parse("00000009-0000-0000-0000-000000000001"), "Movie", file, null);

        var result = Controller(_token).Stream("00000009-0000-0000-0000-000000000001");

        Assert.Equal(contentType, Assert.IsType<PhysicalFileResult>(result).ContentType);
    }

    [Fact]
    public void Stream_FileMissingOnDisk_Returns503()
    {
        File.Delete(_movieFile);

        var result = Controller(_token).Stream(MovieId.ToString("N"));

        Assert.Equal(503, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public void Stream_UnreadableFile_Returns503()
    {
        using var exclusive = new FileStream(_movieFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = Controller(_token).Stream(MovieId.ToString("N"));

        Assert.Equal(503, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public void Stream_SecondItemOverLimit_Returns429WithRetryAfter()
    {
        Assert.IsType<PhysicalFileResult>(Controller(_token).Stream(MovieId.ToString("N")));
        var controller = Controller(_token);

        var result = controller.Stream(Movie2Id.ToString("N"));

        Assert.Equal(429, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        Assert.Equal("30", controller.Response.Headers.RetryAfter.ToString());
    }

    [Fact]
    public void Stream_SameItemAgain_IsStillServed()
    {
        Assert.IsType<PhysicalFileResult>(Controller(_token).Stream(MovieId.ToString("N")));

        Assert.IsType<PhysicalFileResult>(Controller(_token).Stream(MovieId.ToString("N")));
        Assert.IsType<PhysicalFileResult>(Controller(_token, "HEAD").Stream(MovieId.ToString("N")));
    }

    [Fact]
    public void Stream_AfterIdle30s_OtherItemIsServed()
    {
        Assert.IsType<PhysicalFileResult>(Controller(_token).Stream(MovieId.ToString("N")));
        CompleteAllResponses();
        _clock.Advance(TimeSpan.FromSeconds(30));

        Assert.IsType<PhysicalFileResult>(Controller(_token).Stream(Movie2Id.ToString("N")));
    }

    [Fact]
    public void Session_HeldWhileBodyInFlight_DoesNotExpire()
    {
        Assert.IsType<PhysicalFileResult>(Controller(_token).Stream(MovieId.ToString("N")));

        _clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(429, Assert.IsAssignableFrom<ObjectResult>(Controller(_token).Stream(Movie2Id.ToString("N"))).StatusCode);
    }

    [Fact]
    public void Session_ReleasedAfterBody_ExpiresAfter30s()
    {
        Assert.IsType<PhysicalFileResult>(Controller(_token).Stream(MovieId.ToString("N")));
        _clock.Advance(TimeSpan.FromMinutes(5));
        CompleteAllResponses();

        _clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(429, Assert.IsAssignableFrom<ObjectResult>(Controller(_token).Stream(Movie2Id.ToString("N"))).StatusCode);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.IsType<PhysicalFileResult>(Controller(_token).Stream(Movie2Id.ToString("N")));
    }

    [Fact]
    public void NewSession_503_ReleasesSlot()
    {
        File.Delete(_movieFile);

        Assert.Equal(503, Assert.IsAssignableFrom<ObjectResult>(Controller(_token).Stream(MovieId.ToString("N"))).StatusCode);

        Assert.Equal(0, _limiter.Active(_config.Current.Shares[0].Id));
        Assert.IsType<PhysicalFileResult>(Controller(_token).Stream(Movie2Id.ToString("N")));
    }

    [Fact]
    public void Stream_InvalidPathCharacters_Returns503_AndReleasesSlot()
    {
        AddItem(Lib, Guid.Parse("00000009-0000-0000-0000-000000000002"), "Movie", "bad\0path.mkv", null);

        var result = Controller(_token).Stream("00000009-0000-0000-0000-000000000002");

        Assert.Equal(503, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        Assert.Equal(0, _limiter.Active(_config.Current.Shares[0].Id));
    }

    [Fact]
    public void ExistingSession_503_KeepsSlot()
    {
        Assert.IsType<PhysicalFileResult>(Controller(_token).Stream(MovieId.ToString("N")));
        CompleteAllResponses();
        File.Delete(_movieFile);

        Assert.Equal(503, Assert.IsAssignableFrom<ObjectResult>(Controller(_token).Stream(MovieId.ToString("N"))).StatusCode);

        Assert.Equal(1, _limiter.Active(_config.Current.Shares[0].Id));
    }

    [Fact]
    public void Stream_MaxStreamsZero_Returns429()
    {
        _config.Settings.Shares[0].MaxStreams = 0;

        Assert.Equal(429, Assert.IsAssignableFrom<ObjectResult>(Controller(_token).Stream(MovieId.ToString("N"))).StatusCode);
    }

    [Fact]
    public async Task Stream_BitrateAboveCap_ServesTranscodeOfItemFile()
    {
        CapMovie();
        var controller = Controller(_token);
        controller.Request.Headers.Range = "bytes=1000-";

        var http = await Execute(controller, controller.Stream(MovieId.ToString("N")));

        Assert.Equal(200, http.Response.StatusCode);
        Assert.Equal("video/mp2t", http.Response.ContentType);
        Assert.Equal("none", http.Response.Headers.AcceptRanges.ToString());
        Assert.False(http.Response.Headers.ContainsKey("Content-Range"));
        Assert.Null(http.Response.ContentLength);
        Assert.Equal(FakeFfmpegRunner.Payload, Body(http));
        var call = Assert.Single(_runner.Calls);
        Assert.Equal("/usr/lib/jellyfin-ffmpeg/ffmpeg", call.Exe);
        Assert.Equal(CappedStreamArgs.Build(_movieFile, 2_000_000, null, HardwareEncoder.None, null), call.Args);
    }

    [Fact]
    public async Task Stream_Capped_StartQuery_SeeksBeforeInput()
    {
        CapMovie();
        var controller = Controller(_token);

        await Execute(controller, controller.Stream(MovieId.ToString("N"), "600.5"));

        var args = Assert.Single(_runner.Calls).Args.ToList();
        Assert.Equal("600.5", args[args.IndexOf("-ss") + 1]);
        Assert.True(args.IndexOf("-ss") < args.IndexOf("-i"));
    }

    [Fact]
    public async Task Stream_Capped_StartBeyondRuntime_IsCappedToRuntime()
    {
        CapMovie();
        var controller = Controller(_token);

        await Execute(controller, controller.Stream(MovieId.ToString("N"), "999999"));

        var args = Assert.Single(_runner.Calls).Args.ToList();
        Assert.Equal("5400", args[args.IndexOf("-ss") + 1]);
    }

    [Theory]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("1;rm")]
    public void Stream_InvalidStart_Returns400_WithoutSession(string start)
    {
        CapMovie();

        var result = Controller(_token).Stream(MovieId.ToString("N"), start);

        Assert.Equal(400, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        Assert.Equal(0, _limiter.Active(_config.Current.Shares[0].Id));
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task Stream_Capped_Head_SendsHeadersWithoutTranscoding()
    {
        CapMovie();
        var controller = Controller(_token, "HEAD");

        var http = await Execute(controller, controller.Stream(MovieId.ToString("N")));

        Assert.Equal(200, http.Response.StatusCode);
        Assert.Equal("video/mp2t", http.Response.ContentType);
        Assert.Empty(Body(http));
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task Stream_Capped_CountsAgainstLimit_WhileInFlight_AndDisconnectStopsRunner()
    {
        CapMovie();
        _runner.BlockUntilCancelled = true;
        using var disconnect = new CancellationTokenSource();
        var controller = Controller(_token, aborted: disconnect.Token);

        var running = Execute(controller, controller.Stream(MovieId.ToString("N")));
        await _runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(429, Assert.IsAssignableFrom<ObjectResult>(Controller(_token).Stream(Movie2Id.ToString("N"))).StatusCode);

        disconnect.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(_runner.SawCancellation);
        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.IsType<PhysicalFileResult>(Controller(_token).Stream(Movie2Id.ToString("N")));
    }

    [Fact]
    public async Task Stream_Capped_FinishedTranscode_ReleasesSessionWithoutResponseCompletion()
    {
        CapMovie();
        var controller = Controller(_token);

        await Execute(controller, controller.Stream(MovieId.ToString("N")));
        _clock.Advance(TimeSpan.FromSeconds(30));

        Assert.IsType<PhysicalFileResult>(Controller(_token).Stream(Movie2Id.ToString("N")));
    }

    [Fact]
    public async Task Stream_Capped_HardwareFailsBeforeOutput_RetriesInSoftware()
    {
        CapMovie();
        _encoder.Settings = _encoder.Settings! with { Hardware = HardwareEncoder.Nvenc };
        _runner.FailFirstCallWithoutOutput = true;
        var controller = Controller(_token);

        var http = await Execute(controller, controller.Stream(MovieId.ToString("N")));

        Assert.Equal(2, _runner.Calls.Count);
        Assert.Contains("h264_nvenc", _runner.Calls[0].Args);
        Assert.Contains("libx264", _runner.Calls[1].Args);
        Assert.Equal(FakeFfmpegRunner.Payload, Body(http));
    }

    [Fact]
    public void Stream_BitrateAboveCap_NoFfmpeg_Returns409_AndDoesNotCountAgainstLimit()
    {
        CapMovie();
        _encoder.Settings = null;

        var result = Controller(_token).Stream(MovieId.ToString("N"));

        Assert.Equal(409, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        Assert.Equal(0, _limiter.Active(_config.Current.Shares[0].Id));
        Assert.IsType<PhysicalFileResult>(Controller(_token).Stream(Movie2Id.ToString("N")));
    }

    [Fact]
    public void Stream_CapTooLowForTranscode_Returns409()
    {
        CapMovie(cap: 200_000);

        Assert.Equal(409, Assert.IsAssignableFrom<ObjectResult>(Controller(_token).Stream(MovieId.ToString("N"))).StatusCode);
        Assert.Equal(0, _limiter.Active(_config.Current.Shares[0].Id));
    }

    [Fact]
    public void Stream_BelowCap_IgnoresStart_AndServesFile()
    {
        _config.Settings.Shares[0].MaxBitrate = 10_000_000;
        _source.Streams[MovieId] = _source.Streams[MovieId] with { Bitrate = 1_000_000 };

        Assert.IsType<PhysicalFileResult>(Controller(_token).Stream(MovieId.ToString("N"), "60"));
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task Stream_UnknownBitrate_WithCap_IsTranscoded()
    {
        _config.Settings.Shares[0].MaxBitrate = 2_000_000;
        _source.Streams[MovieId] = _source.Streams[MovieId] with { Bitrate = null };
        var controller = Controller(_token);

        var result = controller.Stream(MovieId.ToString("N"));

        Assert.IsNotType<PhysicalFileResult>(result);
        var http = await Execute(controller, result);
        Assert.Equal("video/mp2t", http.Response.ContentType);
        Assert.Single(_runner.Calls);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public async Task Stream_ZeroOrNegativeBitrate_WithCap_IsTranscoded(long bitrate)
    {
        _config.Settings.Shares[0].MaxBitrate = 2_000_000;
        _source.Streams[MovieId] = _source.Streams[MovieId] with { Bitrate = bitrate };
        var controller = Controller(_token);

        var result = controller.Stream(MovieId.ToString("N"));

        Assert.IsNotType<PhysicalFileResult>(result);
        await Execute(controller, result);
        Assert.Single(_runner.Calls);
    }

    [Fact]
    public void Stream_UnknownBitrate_WithoutCap_ServesFile()
    {
        _source.Streams[MovieId] = _source.Streams[MovieId] with { Bitrate = null };

        Assert.IsType<PhysicalFileResult>(Controller(_token).Stream(MovieId.ToString("N")));
    }

    [Fact]
    public async Task Stream_Capped_TwoParallelCappedGets_BothRun()
    {
        CapMovie();
        _runner.BlockUntilCancelled = true;
        var first = Controller(_token);
        var second = Controller(_token);

        var firstRun = Execute(first, first.Stream(MovieId.ToString("N")));
        await _runner.WaitForStartsAsync(1);
        var secondRun = Execute(second, second.Stream(MovieId.ToString("N"), "300"));
        await _runner.WaitForStartsAsync(1);

        Assert.Equal(0, _runner.Cancellations);
        Assert.Equal(2, _capped!.RunningTranscodes);
        Assert.False(firstRun.IsCompleted);
        Assert.False(secondRun.IsCompleted);
        Assert.Equal(1, _limiter.Active(_config.Current.Shares[0].Id));

        _abort.Cancel();
        await Task.WhenAll(firstRun, secondRun).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, _capped.RunningTranscodes);
    }

    [Fact]
    public async Task Stream_Capped_ThirdCappedGet_CancelsOldest()
    {
        CapMovie();
        _runner.BlockUntilCancelled = true;
        var controllers = new[] { Controller(_token), Controller(_token), Controller(_token) };
        var runs = new List<Task>();
        foreach (var controller in controllers)
        {
            runs.Add(Execute(controller, controller.Stream(MovieId.ToString("N"))));
            await _runner.WaitForStartsAsync(1);
        }

        await runs[0].WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, _runner.Cancellations);
        Assert.Equal(2, _capped!.RunningTranscodes);
        Assert.True(_lifetimes[0].Aborted, "superseded response must be aborted, not ended as if complete");
        Assert.False(_lifetimes[1].Aborted);
        Assert.False(_lifetimes[2].Aborted);
        Assert.False(runs[1].IsCompleted);
        Assert.False(runs[2].IsCompleted);

        _abort.Cancel();
        await Task.WhenAll(runs).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, _capped.RunningTranscodes);
    }

    [Fact]
    public async Task Stream_Capped_ManyParallelGetsForSameSession_NeverRunMoreThanTwo()
    {
        CapMovie();
        _runner.BlockUntilCancelled = true;
        var runs = new List<Task>();
        for (var i = 0; i < 10; i++)
        {
            var controller = Controller(_token);
            runs.Add(Execute(controller, controller.Stream(MovieId.ToString("N"))));
            await _runner.WaitForStartsAsync(1);
            Assert.True(_capped!.RunningTranscodes <= 2);
        }

        await Task.WhenAll(runs.Take(8)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, _capped!.RunningTranscodes);
        Assert.Equal(8, _runner.Cancellations);
        Assert.False(runs[8].IsCompleted);
        Assert.False(runs[9].IsCompleted);

        _abort.Cancel();
        await Task.WhenAll(runs).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Stream_Capped_SecondTranscodeOfSession_CountsAgainstGlobalLimit()
    {
        _config.Settings.MaxConcurrentTranscodes = 1;
        CapMovie();
        _runner.BlockUntilCancelled = true;
        var first = Controller(_token);
        var run = Execute(first, first.Stream(MovieId.ToString("N")));
        await _runner.WaitForStartsAsync(1);

        var second = Controller(_token);
        Assert.Equal(429, Assert.IsAssignableFrom<ObjectResult>(second.Stream(MovieId.ToString("N"))).StatusCode);
        Assert.Equal(0, _runner.Cancellations);

        _abort.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Stream_Capped_OverGlobalTranscodeLimit_Returns429_AndReleasesSlot()
    {
        _config.Settings.MaxConcurrentTranscodes = 2;
        _config.Settings.Shares[0].MaxStreams = 10;
        _config.Settings.Shares[0].MaxBitrate = 2_000_000;
        foreach (var id in new[] { MovieId, Movie2Id, EpisodeId })
        {
            _source.Streams[id] = _source.Streams[id] with { Bitrate = 8_000_000 };
        }

        _runner.BlockUntilCancelled = true;
        var a = Controller(_token);
        var b = Controller(_token);
        var runA = Execute(a, a.Stream(MovieId.ToString("N")));
        var runB = Execute(b, b.Stream(Movie2Id.ToString("N")));
        await _runner.WaitForStartsAsync(2);
        var c = Controller(_token);

        var result = c.Stream(EpisodeId.ToString("N"));

        Assert.Equal(429, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        Assert.Equal("30", c.Response.Headers.RetryAfter.ToString());
        Assert.Equal(2, _limiter.Active(_config.Current.Shares[0].Id));

        _abort.Cancel();
        await Task.WhenAll(runA, runB).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, _capped!.RunningTranscodes);
    }

    [Fact]
    public async Task Stream_Capped_Head_DoesNotCancelRunningTranscode()
    {
        CapMovie();
        _runner.BlockUntilCancelled = true;
        var get = Controller(_token);
        var run = Execute(get, get.Stream(MovieId.ToString("N")));
        await _runner.WaitForStartsAsync(1);

        var head = Controller(_token, "HEAD");
        await Execute(head, head.Stream(MovieId.ToString("N")));

        Assert.Equal(0, _runner.Cancellations);
        _abort.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Stream_Capped_FailedTranscodeWithoutOutput_Returns503_AndReleasesNewSession()
    {
        CapMovie();
        _runner.FailAlwaysWithoutOutput = true;
        var controller = Controller(_token);

        var http = await Execute(controller, controller.Stream(MovieId.ToString("N")));

        Assert.Equal(503, http.Response.StatusCode);
        Assert.Null(http.Response.ContentType);
        Assert.False(http.Response.Headers.ContainsKey("Accept-Ranges"));
        Assert.Empty(Body(http));
        Assert.Equal(0, _limiter.Active(_config.Current.Shares[0].Id));
    }

    [Fact]
    public async Task Stream_Capped_FfmpegCannotStart_Returns503()
    {
        CapMovie();
        _runner.ThrowStartFailure = true;
        var controller = Controller(_token);

        var http = await Execute(controller, controller.Stream(MovieId.ToString("N")));

        Assert.Equal(503, http.Response.StatusCode);
        Assert.Null(http.Response.ContentType);
        Assert.Equal(0, _capped!.RunningTranscodes);
    }

    [Fact]
    public async Task Stream_Capped_HardwareFailure_WarnsOnce_AndSkipsHardwareFor10Minutes()
    {
        CapMovie();
        _encoder.Settings = _encoder.Settings! with { Hardware = HardwareEncoder.Nvenc };
        _runner.FailHardwareWithoutOutput = true;

        for (var i = 0; i < 3; i++)
        {
            var controller = Controller(_token);
            await Execute(controller, controller.Stream(MovieId.ToString("N")));
        }

        Assert.Equal(4, _runner.Calls.Count);
        Assert.Contains("h264_nvenc", _runner.Calls[0].Args);
        Assert.All(_runner.Calls.Skip(1), c => Assert.Contains("libx264", c.Args));
        Assert.Single(_cappedLogger.Entries, e => e.Level == LogLevel.Warning);

        _clock.Advance(TimeSpan.FromMinutes(10));
        var later = Controller(_token);
        await Execute(later, later.Stream(MovieId.ToString("N")));
        Assert.Contains("h264_nvenc", _runner.Calls[4].Args);
    }

    [Theory]
    [InlineData(1_000_000L)]
    [InlineData(500_000L)]
    public void Stream_BitrateAtOrBelowCap_IsServed(long? bitrate)
    {
        _config.Settings.Shares[0].MaxBitrate = 1_000_000;
        _source.Streams[MovieId] = _source.Streams[MovieId] with { Bitrate = bitrate };

        Assert.IsType<PhysicalFileResult>(Controller(_token).Stream(MovieId.ToString("N")));
    }

    [Fact]
    public void Stream_NewSession_LogsShareAndItemName_OncePerSession_NoToken()
    {
        Controller(_token).Stream(MovieId.ToString("N"));
        Controller(_token).Stream(MovieId.ToString("N"));
        Controller(_token).Stream(MovieId.ToString("N"));

        var line = Assert.Single(_logger.Lines);
        Assert.Contains("bob", line);
        Assert.Contains("Name of 00000001", line);
        Assert.DoesNotContain(_token, line);
    }

    [Fact]
    public void Stream_IsAnonymousGetAndHead()
    {
        var stream = typeof(FederationController).GetMethod(nameof(FederationController.Stream))!;

        Assert.NotNull(stream.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Equal("stream/{itemId}", stream.GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.Equal("stream/{itemId}", stream.GetCustomAttribute<HttpHeadAttribute>()!.Template);
    }

    private sealed class ListLogger : ILogger<FederationController>
    {
        public List<string> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Information)
            {
                Lines.Add(formatter(state, exception));
            }
        }
    }
}
