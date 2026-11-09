using System.Reflection;
using JellyShare.Application.Features.Catalog;
using JellyShare.Application.Features.Streaming;
using JellyShare.Domain.Enums;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Library;
using JellyShare.Jellyfin.Controllers.V1;
using JellyShare.JellyfinTests.Fakes;
using JellyShare.UnitTests.Fakes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace JellyShare.JellyfinTests.Controllers;

public sealed class FederationControllerStreamTests : IDisposable
{
    private static readonly Guid _lib = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid _movieId = Guid.Parse("00000001-0000-0000-0000-000000000001");
    private static readonly Guid _movie2Id = Guid.Parse("00000001-0000-0000-0000-000000000002");
    private static readonly Guid _episodeId = Guid.Parse("00000002-0000-0000-0000-000000000001");
    private static readonly Guid _seriesId = Guid.Parse("00000003-0000-0000-0000-000000000001");
    private static readonly Guid _outsideId = Guid.Parse("00000004-0000-0000-0000-000000000001");

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
            UseDefaultTypes = true,
            MaxStreams = 1,
        });

        _movieFile = Path.Combine(_tempDir, "a.mkv");
        _movie2File = Path.Combine(_tempDir, "b.mp4");
        File.WriteAllBytes(_movieFile, new byte[4096]);
        File.WriteAllBytes(_movie2File, new byte[4096]);
        AddItem(_lib, _movieId, "Movie", _movieFile, null);
        AddItem(_lib, _movie2Id, "Movie", _movie2File, null);
        AddItem(_lib, _episodeId, "Episode", Path.Combine(_tempDir, "e.mkv"), _seriesId);
        File.WriteAllBytes(Path.Combine(_tempDir, "e.mkv"), new byte[10]);
        AddItem(Guid.NewGuid(), _outsideId, "Photo", _movieFile, null);
    }

    public void Dispose()
    {
        _abort.Cancel();
        foreach (var lifetime in _lifetimes)
        {
            lifetime.Dispose();
        }

        _abort.Dispose();
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

    private FederationController Controller(string? token, string method = "GET") => AbortableController(token, method, CancellationToken.None);

    private FederationController AbortableController(string? token, string method, CancellationToken aborted)
    {
        var builder = new CatalogBuilder(_source, new FakeIdentity(), _config, new FakeLibraries(), _ => true);
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
        _source.Streams[_movieId] = _source.Streams[_movieId] with { Bitrate = bitrate, RunTimeTicks = TimeSpan.FromMinutes(90).Ticks };
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
        var result = Controller(null).Stream(_movieId.ToString("N"));

        result.ShouldBeAssignableTo<ObjectResult>().StatusCode.ShouldBe(401);
    }

    [Fact]
    public void Stream_WrongToken_Returns401_AndDoesNotEchoToken()
    {
        const string wrong = "js1_not-a-real-token-value";

        var result = Controller(wrong).Stream(_movieId.ToString("N")).ShouldBeAssignableTo<ObjectResult>();

        result.StatusCode.ShouldBe(401);
        System.Text.Json.JsonSerializer.Serialize(result.Value).ShouldNotContain(wrong, Case.Sensitive);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("../../etc/passwd")]
    public void Stream_BadItemId_Returns404(string id)
    {
        Controller(_token).Stream(id).ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public void Stream_UnknownItem_Returns404()
    {
        Controller(_token).Stream(Guid.NewGuid().ToString("N")).ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public void Stream_ItemOutsideShare_Returns404()
    {
        Controller(_token).Stream(_outsideId.ToString("N")).ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public void Stream_SeriesId_Returns404_EvenThoughImagesAreExposed()
    {
        Controller(_token).Stream(_seriesId.ToString("N")).ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public void Stream_Movie_ServesFileWithRangeProcessing()
    {
        var result = Controller(_token).Stream(_movieId.ToString("N"));

        var file = result.ShouldBeOfType<PhysicalFileResult>();
        file.FileName.ShouldBe(_movieFile);
        file.ContentType.ShouldBe("video/x-matroska");
        file.EnableRangeProcessing.ShouldBeTrue();
    }

    [Fact]
    public void Stream_Episode_IsServed()
    {
        Controller(_token).Stream(_episodeId.ToString("N")).ShouldBeOfType<PhysicalFileResult>();
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
        AddItem(_lib, Guid.Parse("00000009-0000-0000-0000-000000000001"), "Movie", file, null);

        var result = Controller(_token).Stream("00000009-0000-0000-0000-000000000001");

        result.ShouldBeOfType<PhysicalFileResult>().ContentType.ShouldBe(contentType);
    }

    [Fact]
    public void Stream_FileMissingOnDisk_Returns503()
    {
        File.Delete(_movieFile);

        var result = Controller(_token).Stream(_movieId.ToString("N"));

        result.ShouldBeAssignableTo<ObjectResult>().StatusCode.ShouldBe(503);
    }

    [Fact]
    public void Stream_UnreadableFile_Returns503()
    {
        using var exclusive = new FileStream(_movieFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = Controller(_token).Stream(_movieId.ToString("N"));

        result.ShouldBeAssignableTo<ObjectResult>().StatusCode.ShouldBe(503);
    }

    [Fact]
    public void Stream_SecondItemOverLimit_Returns429WithRetryAfter()
    {
        Controller(_token).Stream(_movieId.ToString("N")).ShouldBeOfType<PhysicalFileResult>();
        var controller = Controller(_token);

        var result = controller.Stream(_movie2Id.ToString("N"));

        result.ShouldBeAssignableTo<ObjectResult>().StatusCode.ShouldBe(429);
        controller.Response.Headers.RetryAfter.ToString().ShouldBe("30");
    }

    [Fact]
    public void Stream_SameItemAgain_IsStillServed()
    {
        Controller(_token).Stream(_movieId.ToString("N")).ShouldBeOfType<PhysicalFileResult>();

        Controller(_token).Stream(_movieId.ToString("N")).ShouldBeOfType<PhysicalFileResult>();
        Controller(_token, "HEAD").Stream(_movieId.ToString("N")).ShouldBeOfType<PhysicalFileResult>();
    }

    [Fact]
    public void Stream_AfterIdle30s_OtherItemIsServed()
    {
        Controller(_token).Stream(_movieId.ToString("N")).ShouldBeOfType<PhysicalFileResult>();
        CompleteAllResponses();
        _clock.Advance(TimeSpan.FromSeconds(30));

        Controller(_token).Stream(_movie2Id.ToString("N")).ShouldBeOfType<PhysicalFileResult>();
    }

    [Fact]
    public void Session_HeldWhileBodyInFlight_DoesNotExpire()
    {
        Controller(_token).Stream(_movieId.ToString("N")).ShouldBeOfType<PhysicalFileResult>();

        _clock.Advance(TimeSpan.FromMinutes(5));

        Controller(_token).Stream(_movie2Id.ToString("N")).ShouldBeAssignableTo<ObjectResult>().StatusCode.ShouldBe(429);
    }

    [Fact]
    public void Session_ReleasedAfterBody_ExpiresAfter30s()
    {
        Controller(_token).Stream(_movieId.ToString("N")).ShouldBeOfType<PhysicalFileResult>();
        _clock.Advance(TimeSpan.FromMinutes(5));
        CompleteAllResponses();

        _clock.Advance(TimeSpan.FromSeconds(29));
        Controller(_token).Stream(_movie2Id.ToString("N")).ShouldBeAssignableTo<ObjectResult>().StatusCode.ShouldBe(429);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Controller(_token).Stream(_movie2Id.ToString("N")).ShouldBeOfType<PhysicalFileResult>();
    }

    [Fact]
    public void NewSession_503_ReleasesSlot()
    {
        File.Delete(_movieFile);

        Controller(_token).Stream(_movieId.ToString("N")).ShouldBeAssignableTo<ObjectResult>().StatusCode.ShouldBe(503);

        _limiter.Active(_config.Current.Shares[0].Id).ShouldBe(0);
        Controller(_token).Stream(_movie2Id.ToString("N")).ShouldBeOfType<PhysicalFileResult>();
    }

    [Fact]
    public void Stream_InvalidPathCharacters_Returns503_AndReleasesSlot()
    {
        AddItem(_lib, Guid.Parse("00000009-0000-0000-0000-000000000002"), "Movie", "bad\0path.mkv", null);

        var result = Controller(_token).Stream("00000009-0000-0000-0000-000000000002");

        result.ShouldBeAssignableTo<ObjectResult>().StatusCode.ShouldBe(503);
        _limiter.Active(_config.Current.Shares[0].Id).ShouldBe(0);
    }

    [Fact]
    public void ExistingSession_503_KeepsSlot()
    {
        Controller(_token).Stream(_movieId.ToString("N")).ShouldBeOfType<PhysicalFileResult>();
        CompleteAllResponses();
        File.Delete(_movieFile);

        Controller(_token).Stream(_movieId.ToString("N")).ShouldBeAssignableTo<ObjectResult>().StatusCode.ShouldBe(503);

        _limiter.Active(_config.Current.Shares[0].Id).ShouldBe(1);
    }

    [Fact]
    public void Stream_MaxStreamsZero_Returns429()
    {
        _config.Settings.Shares[0].MaxStreams = 0;

        Controller(_token).Stream(_movieId.ToString("N")).ShouldBeAssignableTo<ObjectResult>().StatusCode.ShouldBe(429);
    }

    [Fact]
    public async Task Stream_BitrateAboveCap_ServesTranscodeOfItemFile()
    {
        CapMovie();
        var controller = Controller(_token);
        controller.Request.Headers.Range = "bytes=1000-";

        var http = await Execute(controller, controller.Stream(_movieId.ToString("N")));

        http.Response.StatusCode.ShouldBe(200);
        http.Response.ContentType.ShouldBe("video/mp2t");
        http.Response.Headers.AcceptRanges.ToString().ShouldBe("none");
        http.Response.Headers.ContainsKey("Content-Range").ShouldBeFalse();
        http.Response.ContentLength.ShouldBeNull();
        Body(http).ShouldBe(FakeFfmpegRunner.Payload);
        var call = _runner.Calls.ShouldHaveSingleItem();
        call.Exe.ShouldBe("/usr/lib/jellyfin-ffmpeg/ffmpeg");
        call.Args.ShouldBe(CappedStreamArgs.Build(_movieFile, 2_000_000, null, HardwareEncoder.None, null));
    }

    [Fact]
    public async Task Stream_Capped_StartQuery_SeeksBeforeInput()
    {
        CapMovie();
        var controller = Controller(_token);

        await Execute(controller, controller.Stream(_movieId.ToString("N"), "600.5"));

        var args = _runner.Calls.ShouldHaveSingleItem().Args.ToList();
        args[args.IndexOf("-ss") + 1].ShouldBe("600.5");
        (args.IndexOf("-ss") < args.IndexOf("-i")).ShouldBeTrue();
    }

    [Fact]
    public async Task Stream_Capped_StartBeyondRuntime_IsCappedToRuntime()
    {
        CapMovie();
        var controller = Controller(_token);

        await Execute(controller, controller.Stream(_movieId.ToString("N"), "999999"));

        var args = _runner.Calls.ShouldHaveSingleItem().Args.ToList();
        args[args.IndexOf("-ss") + 1].ShouldBe("5400");
    }

    [Theory]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("1;rm")]
    public void Stream_InvalidStart_Returns400_WithoutSession(string start)
    {
        CapMovie();

        var result = Controller(_token).Stream(_movieId.ToString("N"), start);

        result.ShouldBeAssignableTo<ObjectResult>().StatusCode.ShouldBe(400);
        _limiter.Active(_config.Current.Shares[0].Id).ShouldBe(0);
        _runner.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Stream_Capped_Head_SendsHeadersWithoutTranscoding()
    {
        CapMovie();
        var controller = Controller(_token, "HEAD");

        var http = await Execute(controller, controller.Stream(_movieId.ToString("N")));

        http.Response.StatusCode.ShouldBe(200);
        http.Response.ContentType.ShouldBe("video/mp2t");
        Body(http).ShouldBeEmpty();
        _runner.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Stream_Capped_CountsAgainstLimit_WhileInFlight_AndDisconnectStopsRunner()
    {
        CapMovie();
        _runner.BlockUntilCancelled = true;
        using var disconnect = new CancellationTokenSource();
        var controller = AbortableController(_token, "GET", disconnect.Token);

        var running = Execute(controller, controller.Stream(_movieId.ToString("N")));
        await _runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(5));
        Controller(_token).Stream(_movie2Id.ToString("N")).ShouldBeAssignableTo<ObjectResult>().StatusCode.ShouldBe(429);

        await disconnect.CancelAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        _runner.SawCancellation.ShouldBeTrue();
        _clock.Advance(TimeSpan.FromSeconds(30));
        Controller(_token).Stream(_movie2Id.ToString("N")).ShouldBeOfType<PhysicalFileResult>();
    }

    [Fact]
    public async Task Stream_Capped_FinishedTranscode_ReleasesSessionWithoutResponseCompletion()
    {
        CapMovie();
        var controller = Controller(_token);

        await Execute(controller, controller.Stream(_movieId.ToString("N")));
        _clock.Advance(TimeSpan.FromSeconds(30));

        Controller(_token).Stream(_movie2Id.ToString("N")).ShouldBeOfType<PhysicalFileResult>();
    }

    [Fact]
    public async Task Stream_Capped_HardwareFailsBeforeOutput_RetriesInSoftware()
    {
        CapMovie();
        _encoder.Settings = _encoder.Settings! with { Hardware = HardwareEncoder.Nvenc };
        _runner.FailFirstCallWithoutOutput = true;
        var controller = Controller(_token);

        var http = await Execute(controller, controller.Stream(_movieId.ToString("N")));

        _runner.Calls.Count.ShouldBe(2);
        _runner.Calls[0].Args.ShouldContain("h264_nvenc");
        _runner.Calls[1].Args.ShouldContain("libx264");
        Body(http).ShouldBe(FakeFfmpegRunner.Payload);
    }

    [Fact]
    public void Stream_BitrateAboveCap_NoFfmpeg_Returns409_AndDoesNotCountAgainstLimit()
    {
        CapMovie();
        _encoder.Settings = null;

        var result = Controller(_token).Stream(_movieId.ToString("N"));

        result.ShouldBeAssignableTo<ObjectResult>().StatusCode.ShouldBe(409);
        _limiter.Active(_config.Current.Shares[0].Id).ShouldBe(0);
        Controller(_token).Stream(_movie2Id.ToString("N")).ShouldBeOfType<PhysicalFileResult>();
    }

    [Fact]
    public void Stream_CapTooLowForTranscode_Returns409()
    {
        CapMovie(cap: 200_000);

        Controller(_token).Stream(_movieId.ToString("N")).ShouldBeAssignableTo<ObjectResult>().StatusCode.ShouldBe(409);
        _limiter.Active(_config.Current.Shares[0].Id).ShouldBe(0);
    }

    [Fact]
    public void Stream_BelowCap_IgnoresStart_AndServesFile()
    {
        _config.Settings.Shares[0].MaxBitrate = 10_000_000;
        _source.Streams[_movieId] = _source.Streams[_movieId] with { Bitrate = 1_000_000 };

        Controller(_token).Stream(_movieId.ToString("N"), "60").ShouldBeOfType<PhysicalFileResult>();
        _runner.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Stream_UnknownBitrate_WithCap_IsTranscoded()
    {
        _config.Settings.Shares[0].MaxBitrate = 2_000_000;
        _source.Streams[_movieId] = _source.Streams[_movieId] with { Bitrate = null };
        var controller = Controller(_token);

        var result = controller.Stream(_movieId.ToString("N"));

        result.ShouldNotBeOfType<PhysicalFileResult>();
        var http = await Execute(controller, result);
        http.Response.ContentType.ShouldBe("video/mp2t");
        _runner.Calls.ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public async Task Stream_ZeroOrNegativeBitrate_WithCap_IsTranscoded(long bitrate)
    {
        _config.Settings.Shares[0].MaxBitrate = 2_000_000;
        _source.Streams[_movieId] = _source.Streams[_movieId] with { Bitrate = bitrate };
        var controller = Controller(_token);

        var result = controller.Stream(_movieId.ToString("N"));

        result.ShouldNotBeOfType<PhysicalFileResult>();
        await Execute(controller, result);
        _runner.Calls.ShouldHaveSingleItem();
    }

    [Fact]
    public void Stream_UnknownBitrate_WithoutCap_ServesFile()
    {
        _source.Streams[_movieId] = _source.Streams[_movieId] with { Bitrate = null };

        Controller(_token).Stream(_movieId.ToString("N")).ShouldBeOfType<PhysicalFileResult>();
    }

    [Fact]
    public async Task Stream_Capped_TwoParallelCappedGets_BothRun()
    {
        CapMovie();
        _runner.BlockUntilCancelled = true;
        var first = Controller(_token);
        var second = Controller(_token);

        var firstRun = Execute(first, first.Stream(_movieId.ToString("N")));
        await _runner.WaitForStartsAsync(1);
        var secondRun = Execute(second, second.Stream(_movieId.ToString("N"), "300"));
        await _runner.WaitForStartsAsync(1);

        _runner.Cancellations.ShouldBe(0);
        _capped!.RunningTranscodes.ShouldBe(2);
        firstRun.IsCompleted.ShouldBeFalse();
        secondRun.IsCompleted.ShouldBeFalse();
        _limiter.Active(_config.Current.Shares[0].Id).ShouldBe(1);

        await _abort.CancelAsync();
        await Task.WhenAll(firstRun, secondRun).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        _capped.RunningTranscodes.ShouldBe(0);
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
            runs.Add(Execute(controller, controller.Stream(_movieId.ToString("N"))));
            await _runner.WaitForStartsAsync(1);
        }

        await runs[0].WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        _runner.Cancellations.ShouldBe(1);
        _capped!.RunningTranscodes.ShouldBe(2);
        _lifetimes[0].Aborted.ShouldBeTrue("superseded response must be aborted, not ended as if complete");
        _lifetimes[1].Aborted.ShouldBeFalse();
        _lifetimes[2].Aborted.ShouldBeFalse();
        runs[1].IsCompleted.ShouldBeFalse();
        runs[2].IsCompleted.ShouldBeFalse();

        await _abort.CancelAsync();
        await Task.WhenAll(runs).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        _capped.RunningTranscodes.ShouldBe(0);
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
            runs.Add(Execute(controller, controller.Stream(_movieId.ToString("N"))));
            await _runner.WaitForStartsAsync(1);
            (_capped!.RunningTranscodes <= 2).ShouldBeTrue();
        }

        await Task.WhenAll(runs.Take(8)).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        _capped!.RunningTranscodes.ShouldBe(2);
        _runner.Cancellations.ShouldBe(8);
        runs[8].IsCompleted.ShouldBeFalse();
        runs[9].IsCompleted.ShouldBeFalse();

        await _abort.CancelAsync();
        await Task.WhenAll(runs).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
    }

    [Fact]
    public async Task Stream_Capped_SecondTranscodeOfSession_CountsAgainstGlobalLimit()
    {
        _config.Settings.MaxConcurrentTranscodes = 1;
        CapMovie();
        _runner.BlockUntilCancelled = true;
        var first = Controller(_token);
        var run = Execute(first, first.Stream(_movieId.ToString("N")));
        await _runner.WaitForStartsAsync(1);

        var second = Controller(_token);
        second.Stream(_movieId.ToString("N")).ShouldBeAssignableTo<ObjectResult>().StatusCode.ShouldBe(429);
        _runner.Cancellations.ShouldBe(0);

        await _abort.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
    }

    [Fact]
    public async Task Stream_Capped_OverGlobalTranscodeLimit_Returns429_AndReleasesSlot()
    {
        _config.Settings.MaxConcurrentTranscodes = 2;
        _config.Settings.Shares[0].MaxStreams = 10;
        _config.Settings.Shares[0].MaxBitrate = 2_000_000;
        foreach (var id in new[] { _movieId, _movie2Id, _episodeId })
        {
            _source.Streams[id] = _source.Streams[id] with { Bitrate = 8_000_000 };
        }

        _runner.BlockUntilCancelled = true;
        var a = Controller(_token);
        var b = Controller(_token);
        var runA = Execute(a, a.Stream(_movieId.ToString("N")));
        var runB = Execute(b, b.Stream(_movie2Id.ToString("N")));
        await _runner.WaitForStartsAsync(2);
        var c = Controller(_token);

        var result = c.Stream(_episodeId.ToString("N"));

        result.ShouldBeAssignableTo<ObjectResult>().StatusCode.ShouldBe(429);
        c.Response.Headers.RetryAfter.ToString().ShouldBe("30");
        _limiter.Active(_config.Current.Shares[0].Id).ShouldBe(2);

        await _abort.CancelAsync();
        await Task.WhenAll(runA, runB).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        _capped!.RunningTranscodes.ShouldBe(0);
    }

    [Fact]
    public async Task Stream_Capped_Head_DoesNotCancelRunningTranscode()
    {
        CapMovie();
        _runner.BlockUntilCancelled = true;
        var get = Controller(_token);
        var run = Execute(get, get.Stream(_movieId.ToString("N")));
        await _runner.WaitForStartsAsync(1);

        var head = Controller(_token, "HEAD");
        await Execute(head, head.Stream(_movieId.ToString("N")));

        _runner.Cancellations.ShouldBe(0);
        await _abort.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
    }

    [Fact]
    public async Task Stream_Capped_FailedTranscodeWithoutOutput_Returns503_AndReleasesNewSession()
    {
        CapMovie();
        _runner.FailAlwaysWithoutOutput = true;
        var controller = Controller(_token);

        var http = await Execute(controller, controller.Stream(_movieId.ToString("N")));

        http.Response.StatusCode.ShouldBe(503);
        http.Response.ContentType.ShouldBeNull();
        http.Response.Headers.ContainsKey("Accept-Ranges").ShouldBeFalse();
        Body(http).ShouldBeEmpty();
        _limiter.Active(_config.Current.Shares[0].Id).ShouldBe(0);
    }

    [Fact]
    public async Task Stream_Capped_FfmpegCannotStart_Returns503()
    {
        CapMovie();
        _runner.ThrowStartFailure = true;
        var controller = Controller(_token);

        var http = await Execute(controller, controller.Stream(_movieId.ToString("N")));

        http.Response.StatusCode.ShouldBe(503);
        http.Response.ContentType.ShouldBeNull();
        _capped!.RunningTranscodes.ShouldBe(0);
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
            await Execute(controller, controller.Stream(_movieId.ToString("N")));
        }

        _runner.Calls.Count.ShouldBe(4);
        _runner.Calls[0].Args.ShouldContain("h264_nvenc");
        foreach (var c in _runner.Calls.Skip(1))
        {
            c.Args.ShouldContain("libx264");
        }
        _cappedLogger.Entries.Where(e => e.Level == LogLevel.Warning).ShouldHaveSingleItem();

        _clock.Advance(TimeSpan.FromMinutes(10));
        var later = Controller(_token);
        await Execute(later, later.Stream(_movieId.ToString("N")));
        _runner.Calls[4].Args.ShouldContain("h264_nvenc");
    }

    [Theory]
    [InlineData(1_000_000L)]
    [InlineData(500_000L)]
    public void Stream_BitrateAtOrBelowCap_IsServed(long? bitrate)
    {
        _config.Settings.Shares[0].MaxBitrate = 1_000_000;
        _source.Streams[_movieId] = _source.Streams[_movieId] with { Bitrate = bitrate };

        Controller(_token).Stream(_movieId.ToString("N")).ShouldBeOfType<PhysicalFileResult>();
    }

    [Fact]
    public void Stream_NewSession_LogsShareAndItemName_OncePerSession_NoToken()
    {
        Controller(_token).Stream(_movieId.ToString("N"));
        Controller(_token).Stream(_movieId.ToString("N"));
        Controller(_token).Stream(_movieId.ToString("N"));

        var line = _logger.Lines.ShouldHaveSingleItem();
        line.ShouldContain("bob", Case.Sensitive);
        line.ShouldContain("Name of 00000001", Case.Sensitive);
        line.ShouldNotContain(_token, Case.Sensitive);
    }

    private Guid AddTrack(int n, Guid? titleId, string type = "Audio", string extension = ".mp3", long? bitrate = 320_000)
    {
        var id = Guid.Parse($"00000005-0000-0000-0000-{n:D12}");
        var file = Path.Combine(_tempDir, $"track{n}{extension}");
        File.WriteAllBytes(file, new byte[64]);
        AddItem(_lib, id, type, file, titleId);
        _source.Streams[id] = new StreamInfo(type, "Track " + n, file, bitrate, null, titleId);
        return id;
    }

    [Fact]
    public void Stream_AudioOverShareCap_IsServedDirect_NotTranscoded()
    {
        _config.Settings.Shares[0].MaxBitrate = 100_000;
        var track = AddTrack(1, Guid.NewGuid());

        var result = Controller(_token).Stream(track.ToString("N"));

        var file = result.ShouldBeOfType<PhysicalFileResult>();
        file.ContentType.ShouldBe("audio/mpeg");
        file.EnableRangeProcessing.ShouldBeTrue();
        _runner.Calls.ShouldBeEmpty();
    }

    [Fact]
    public void Stream_AudioWithUnknownBitrate_UnderCap_IsServedDirect()
    {
        _config.Settings.Shares[0].MaxBitrate = 100_000;
        var track = AddTrack(1, null, bitrate: null);

        Controller(_token).Stream(track.ToString("N")).ShouldBeOfType<PhysicalFileResult>();
    }

    [Theory]
    [InlineData(".flac", "audio/flac")]
    [InlineData(".m4b", "audio/mp4")]
    [InlineData(".opus", "audio/ogg")]
    [InlineData(".xyzunknown", "application/octet-stream")]
    public void Stream_AudioContentTypeFromExtension(string extension, string contentType)
    {
        var track = AddTrack(1, null, "AudioBook", extension);

        Controller(_token).Stream(track.ToString("N")).ShouldBeOfType<PhysicalFileResult>().ContentType.ShouldBe(contentType);
    }

    [Fact]
    public void Stream_AudioTracksOfOneAlbum_ShareOneLease()
    {
        var albumA = Guid.NewGuid();
        var track1 = AddTrack(1, albumA);
        var track2 = AddTrack(2, albumA);
        var otherAlbum = AddTrack(3, Guid.NewGuid());

        Controller(_token).Stream(track1.ToString("N")).ShouldBeOfType<PhysicalFileResult>();
        Controller(_token).Stream(track2.ToString("N")).ShouldBeOfType<PhysicalFileResult>();
        Controller(_token).Stream(otherAlbum.ToString("N")).ShouldBeAssignableTo<ObjectResult>().StatusCode.ShouldBe(429);
        _limiter.Active(_config.Current.Shares[0].Id).ShouldBe(1);
    }

    [Fact]
    public void Stream_AudioNotShared_404()
    {
        var share = _config.Settings.Shares[0];
        share.UseDefaultTypes = false;
        share.Types = new List<MediaType> { MediaType.Movies, MediaType.Shows };
        var track = AddTrack(1, null);

        Controller(_token).Stream(track.ToString("N")).ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public void Stream_IsAnonymousGetAndHead()
    {
        var stream = typeof(FederationController).GetMethod(nameof(FederationController.Stream))!;

        stream.GetCustomAttribute<AllowAnonymousAttribute>().ShouldNotBeNull();
        stream.GetCustomAttribute<HttpGetAttribute>()!.Template.ShouldBe("stream/{itemId}");
        stream.GetCustomAttribute<HttpHeadAttribute>()!.Template.ShouldBe("stream/{itemId}");
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
