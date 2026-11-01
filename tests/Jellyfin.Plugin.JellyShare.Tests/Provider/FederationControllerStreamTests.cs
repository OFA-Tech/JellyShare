using System.Reflection;
using Jellyfin.Plugin.JellyShare.Configuration;
using Jellyfin.Plugin.JellyShare.Protocol;
using Jellyfin.Plugin.JellyShare.Provider;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.JellyShare.Tests.Provider;

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
        _config.Current.Shares.Add(new PeerShare
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
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private void AddItem(Guid library, Guid id, string type, string path, Guid? seriesId)
    {
        var item = new LocalItem(id, type, path, seriesId, () => throw new InvalidOperationException("catalog entry not needed"));
        _source.Items.Add((library, item));
        _source.Streams[id] = new StreamInfo(type, "Name of " + id.ToString("N")[..8], path, null);
    }

    private FederationController Controller(string? token, string method = "GET")
    {
        var builder = new CatalogBuilder(_source, new FakeIdentity(), _config, _ => true);
        var controller = new FederationController(new FakeIdentity(), builder, _source, _config, _tokens, _limiter, _logger);
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        if (token is not null)
        {
            http.Request.Headers["X-JellyShare-Token"] = token;
        }

        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

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
        _clock.Advance(TimeSpan.FromSeconds(30));

        Assert.IsType<PhysicalFileResult>(Controller(_token).Stream(Movie2Id.ToString("N")));
    }

    [Fact]
    public void Stream_MaxStreamsZero_Returns429()
    {
        _config.Current.Shares[0].MaxStreams = 0;

        Assert.Equal(429, Assert.IsAssignableFrom<ObjectResult>(Controller(_token).Stream(MovieId.ToString("N"))).StatusCode);
    }

    [Fact]
    public void Stream_BitrateAboveCap_Returns409_AndDoesNotCountAgainstLimit()
    {
        _config.Current.Shares[0].MaxBitrate = 1_000_000;
        _source.Streams[MovieId] = _source.Streams[MovieId] with { Bitrate = 8_000_000 };

        var result = Controller(_token).Stream(MovieId.ToString("N"));

        Assert.Equal(409, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        Assert.Equal(0, _limiter.Active(_config.Current.Shares[0].Id));
        Assert.IsType<PhysicalFileResult>(Controller(_token).Stream(Movie2Id.ToString("N")));
    }

    [Theory]
    [InlineData(1_000_000L)]
    [InlineData(500_000L)]
    [InlineData(null)]
    public void Stream_BitrateAtOrBelowCapOrUnknown_IsServed(long? bitrate)
    {
        _config.Current.Shares[0].MaxBitrate = 1_000_000;
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
