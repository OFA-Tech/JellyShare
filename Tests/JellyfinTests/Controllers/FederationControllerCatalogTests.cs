using System.Reflection;
using JellyShare.Application.Features.Catalog;
using JellyShare.Application.Features.Streaming;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Library;
using JellyShare.Domain.Models.Protocol;
using JellyShare.Jellyfin.Controllers.V1;
using JellyShare.JellyfinTests.Fakes;
using JellyShare.UnitTests.Fakes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace JellyShare.JellyfinTests.Controllers;

public sealed class FederationControllerCatalogTests : IDisposable
{
    private static readonly Guid _lib = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid _movieId = Guid.Parse("00000001-0000-0000-0000-000000000001");
    private static readonly Guid _episodeId = Guid.Parse("00000002-0000-0000-0000-000000000001");
    private static readonly Guid _seriesId = Guid.Parse("00000003-0000-0000-0000-000000000001");
    private static readonly Guid _outsideId = Guid.Parse("00000004-0000-0000-0000-000000000001");
    private static readonly DateTime _modified = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private readonly FakeSource _source = new();
    private readonly FakeConfig _config = new();
    private readonly ShareTokenService _tokens = new();
    private readonly string _token;
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "jellyshare-test-" + Guid.NewGuid().ToString("N"));

    public FederationControllerCatalogTests()
    {
        Directory.CreateDirectory(_tempDir);
        var (token, hash) = _tokens.Create();
        _token = token;
        _config.Settings.Shares.Add(new PeerShare
        {
            Id = Guid.NewGuid(),
            Name = "bob",
            TokenHash = hash,
            Enabled = true,
            LibraryIds = new List<Guid> { _lib },
        });

        _source.Items.Add((_lib, Movie(_movieId, "/media/Movies/A/A.mkv")));
        _source.Items.Add((_lib, Episode(_episodeId, _seriesId, "/media/Shows/S/Season 01/S.mkv")));
        _source.Items.Add((Guid.NewGuid(), Movie(_outsideId, "/media/Other/B.mkv")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private int _materialized;

    private LocalItem Movie(Guid id, string path)
        => new(id, "Movie", path, null, () =>
        {
            Interlocked.Increment(ref _materialized);
            return Make(id, "Movie", null);
        })
        { DateModified = _modified };

    private LocalItem Episode(Guid id, Guid seriesId, string path)
        => new(id, "Episode", path, seriesId, () =>
        {
            Interlocked.Increment(ref _materialized);
            return Make(id, "Episode", seriesId.ToString("N"));
        })
        { DateModified = _modified };

    private static CatalogItem Make(Guid id, string type, string? seriesId)
        => new(
            id.ToString("N"),
            type,
            "n",
            null,
            new Dictionary<string, string>(),
            null,
            null,
            null,
            null,
            null,
            null,
            new MediaInfo(null, null, null, null, null, Array.Empty<AudioTrack>(), Array.Empty<SubtitleTrack>()),
            Array.Empty<string>(),
            _modified,
            seriesId);

    private FederationController Controller(string? token, Action<HttpContext>? tweak = null)
    {
        var builder = new CatalogBuilder(_source, new FakeIdentity(), _config, _ => true);
        var controller = new FederationController(FederationSender.Create(new FakeIdentity(), builder, _config, _tokens), builder, _source, _config, _tokens, new StreamLimiter(TimeProvider.System), FakeCapped.Create(), Microsoft.Extensions.Logging.Abstractions.NullLogger<FederationController>.Instance);
        var http = new DefaultHttpContext();
        if (token is not null)
        {
            http.Request.Headers["X-JellyShare-Token"] = token;
        }

        tweak?.Invoke(http);
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    [Fact]
    public async Task Catalog_NoToken_Returns401()
    {
        var result = (await Controller(null).Catalog());

        result.ShouldBeAssignableTo<ObjectResult>().StatusCode.ShouldBe(401);
    }

    [Fact]
    public async Task Catalog_WrongToken_Returns401_AndDoesNotEchoToken()
    {
        const string wrong = "js1_not-a-real-token-value";
        var controller = Controller(wrong);

        var result = (await controller.Catalog()).ShouldBeAssignableTo<ObjectResult>();

        result.StatusCode.ShouldBe(401);
        System.Text.Json.JsonSerializer.Serialize(result.Value).ShouldNotContain(wrong, Case.Sensitive);
    }

    [Fact]
    public async Task Catalog_DisabledShare_Returns401()
    {
        _config.Settings.Shares[0].Enabled = false;

        var result = (await Controller(_token).Catalog());

        result.ShouldBeAssignableTo<ObjectResult>().StatusCode.ShouldBe(401);
    }

    [Fact]
    public async Task Catalog_ValidToken_ReturnsOnlyShareItemsWithQuotedEtag()
    {
        var controller = Controller(_token);

        var result = (await controller.Catalog()).ShouldBeOfType<OkObjectResult>();

        var catalog = result.Value.ShouldBeOfType<CatalogResponse>();
        catalog.Items.Count.ShouldBe(2);
        catalog.Items.ShouldNotContain(i => i.Id == _outsideId.ToString("N"));
        controller.Response.Headers.ETag.ToString().ShouldBe("\"" + catalog.Etag + "\"");
    }

    [Fact]
    public async Task Catalog_IfNoneMatchMatches_Returns304WithoutBody()
    {
        var etag = (await Controller(_token).Catalog()).ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<CatalogResponse>().Etag;
        var controller = Controller(_token, h => h.Request.Headers.IfNoneMatch = "\"" + etag + "\"");

        var result = (await controller.Catalog());

        result.ShouldBeOfType<StatusCodeResult>().StatusCode.ShouldBe(304);
        controller.Response.Headers.ETag.ToString().ShouldBe("\"" + etag + "\"");
    }

    [Fact]
    public async Task Catalog_304_MaterializesNothing()
    {
        var etag = (await Controller(_token).Catalog()).ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<CatalogResponse>().Etag;
        _materialized = 0;

        // A fresh builder (no cache): the 304 answer must still not build any catalog entry.
        var controller = Controller(_token, h => h.Request.Headers.IfNoneMatch = "\"" + etag + "\"");

        (await controller.Catalog()).ShouldBeOfType<StatusCodeResult>().StatusCode.ShouldBe(304);
        _materialized.ShouldBe(0);
    }

    [Fact]
    public async Task Catalog_IfNoneMatchStale_Returns200()
    {
        var controller = Controller(_token, h => h.Request.Headers.IfNoneMatch = "\"deadbeef\"");

        (await controller.Catalog()).ShouldBeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Catalog_RevocationIsImmediate()
    {
        (await Controller(_token).Catalog()).ShouldBeOfType<OkObjectResult>();

        _config.Settings.Shares.Clear();

        (await Controller(_token).Catalog()).ShouldBeAssignableTo<ObjectResult>().StatusCode.ShouldBe(401);
    }

    [Fact]
    public void Image_NoToken_Returns401()
    {
        var result = Controller(null).Image(_movieId.ToString("N"), "Primary");

        result.ShouldBeAssignableTo<ObjectResult>().StatusCode.ShouldBe(401);
    }

    [Fact]
    public void Image_UnknownImageType_Returns400()
    {
        var result = Controller(_token).Image(_movieId.ToString("N"), "Banner");

        result.ShouldBeAssignableTo<ObjectResult>().StatusCode.ShouldBe(400);
    }

    [Fact]
    public void Image_BadItemId_Returns404()
    {
        var result = Controller(_token).Image("../../etc/passwd", "Primary");

        result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public void Image_ItemOutsideCatalog_Returns404_EvenIfImageExists()
    {
        var file = Path.Combine(_tempDir, "poster.jpg");
        File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
        _source.Images[(_outsideId, "Primary")] = file;

        var result = Controller(_token).Image(_outsideId.ToString("N"), "Primary");

        result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public void Image_ItemWithoutThatImage_Returns404()
    {
        var result = Controller(_token).Image(_movieId.ToString("N"), "Logo");

        result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public void Image_UnreadableFile_Returns404()
    {
        var file = Path.Combine(_tempDir, "locked.jpg");
        File.WriteAllBytes(file, new byte[] { 1 });
        _source.Images[(_movieId, "Primary")] = file;
        using var exclusive = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = Controller(_token).Image(_movieId.ToString("N"), "Primary");

        result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public void Image_MissingFileOnDisk_Returns404()
    {
        _source.Images[(_movieId, "Primary")] = Path.Combine(_tempDir, "gone.jpg");

        var result = Controller(_token).Image(_movieId.ToString("N"), "Primary");

        result.ShouldBeOfType<NotFoundResult>();
    }

    [Theory]
    [InlineData("poster.jpg", "image/jpeg")]
    [InlineData("poster.png", "image/png")]
    [InlineData("poster.webp", "image/webp")]
    public void Image_ServesFileWithContentType(string name, string contentType)
    {
        var file = Path.Combine(_tempDir, name);
        File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
        _source.Images[(_movieId, "Primary")] = file;

        var result = Controller(_token).Image(_movieId.ToString("N"), "primary");

        var physical = result.ShouldBeOfType<PhysicalFileResult>();
        physical.FileName.ShouldBe(file);
        physical.ContentType.ShouldBe(contentType);
    }

    [Fact]
    public void Image_SeriesOfSharedEpisode_IsServed()
    {
        var file = Path.Combine(_tempDir, "backdrop.jpg");
        File.WriteAllBytes(file, new byte[] { 1 });
        _source.Images[(_seriesId, "Backdrop")] = file;

        var result = Controller(_token).Image(_seriesId.ToString("N"), "Backdrop");

        result.ShouldBeOfType<PhysicalFileResult>();
    }

    [Fact]
    public void Endpoints_AreAnonymousGets()
    {
        var type = typeof(FederationController);

        var catalog = type.GetMethod(nameof(FederationController.Catalog))!;
        catalog.GetCustomAttribute<AllowAnonymousAttribute>().ShouldNotBeNull();
        catalog.GetCustomAttribute<HttpGetAttribute>()!.Template.ShouldBe("catalog");

        var image = type.GetMethod(nameof(FederationController.Image))!;
        image.GetCustomAttribute<AllowAnonymousAttribute>().ShouldNotBeNull();
        image.GetCustomAttribute<HttpGetAttribute>()!.Template.ShouldBe("image/{itemId}/{imageType}");
    }
}
