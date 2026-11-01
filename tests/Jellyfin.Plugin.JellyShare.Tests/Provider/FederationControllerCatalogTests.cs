using System.Reflection;
using Jellyfin.Plugin.JellyShare.Configuration;
using Jellyfin.Plugin.JellyShare.Protocol;
using Jellyfin.Plugin.JellyShare.Provider;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.JellyShare.Tests.Provider;

public sealed class FederationControllerCatalogTests : IDisposable
{
    private static readonly Guid Lib = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid MovieId = Guid.Parse("00000001-0000-0000-0000-000000000001");
    private static readonly Guid EpisodeId = Guid.Parse("00000002-0000-0000-0000-000000000001");
    private static readonly Guid SeriesId = Guid.Parse("00000003-0000-0000-0000-000000000001");
    private static readonly Guid OutsideId = Guid.Parse("00000004-0000-0000-0000-000000000001");
    private static readonly DateTime Modified = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

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
        _config.Current.Shares.Add(new PeerShare
        {
            Id = Guid.NewGuid(),
            Name = "bob",
            TokenHash = hash,
            Enabled = true,
            LibraryIds = new List<Guid> { Lib },
        });

        _source.Items.Add((Lib, Movie(MovieId, "/media/Movies/A/A.mkv")));
        _source.Items.Add((Lib, Episode(EpisodeId, SeriesId, "/media/Shows/S/Season 01/S.mkv")));
        _source.Items.Add((Guid.NewGuid(), Movie(OutsideId, "/media/Other/B.mkv")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private static LocalItem Movie(Guid id, string path)
        => new(id, "Movie", path, null, () => Make(id, "Movie", null));

    private static LocalItem Episode(Guid id, Guid seriesId, string path)
        => new(id, "Episode", path, seriesId, () => Make(id, "Episode", seriesId.ToString("N")));

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
            Modified,
            seriesId);

    private FederationController Controller(string? token, Action<HttpContext>? tweak = null)
    {
        var builder = new CatalogBuilder(_source, new FakeIdentity(), _config, _ => true);
        var controller = new FederationController(new FakeIdentity(), builder, _source, _config, _tokens, new StreamLimiter(TimeProvider.System), FakeCapped.Create(), Microsoft.Extensions.Logging.Abstractions.NullLogger<FederationController>.Instance);
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
    public void Catalog_NoToken_Returns401()
    {
        var result = Controller(null).Catalog();

        Assert.Equal(401, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public void Catalog_WrongToken_Returns401_AndDoesNotEchoToken()
    {
        const string wrong = "js1_not-a-real-token-value";
        var controller = Controller(wrong);

        var result = Assert.IsAssignableFrom<ObjectResult>(controller.Catalog());

        Assert.Equal(401, result.StatusCode);
        Assert.DoesNotContain(wrong, System.Text.Json.JsonSerializer.Serialize(result.Value));
    }

    [Fact]
    public void Catalog_DisabledShare_Returns401()
    {
        _config.Current.Shares[0].Enabled = false;

        var result = Controller(_token).Catalog();

        Assert.Equal(401, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public void Catalog_ValidToken_ReturnsOnlyShareItemsWithQuotedEtag()
    {
        var controller = Controller(_token);

        var result = Assert.IsType<OkObjectResult>(controller.Catalog());

        var catalog = Assert.IsType<CatalogResponse>(result.Value);
        Assert.Equal(2, catalog.Items.Count);
        Assert.DoesNotContain(catalog.Items, i => i.Id == OutsideId.ToString("N"));
        Assert.Equal("\"" + catalog.Etag + "\"", controller.Response.Headers.ETag.ToString());
    }

    [Fact]
    public void Catalog_IfNoneMatchMatches_Returns304WithoutBody()
    {
        var etag = Assert.IsType<CatalogResponse>(Assert.IsType<OkObjectResult>(Controller(_token).Catalog()).Value).Etag;
        var controller = Controller(_token, h => h.Request.Headers.IfNoneMatch = "\"" + etag + "\"");

        var result = controller.Catalog();

        Assert.Equal(304, Assert.IsType<StatusCodeResult>(result).StatusCode);
        Assert.Equal("\"" + etag + "\"", controller.Response.Headers.ETag.ToString());
    }

    [Fact]
    public void Catalog_IfNoneMatchStale_Returns200()
    {
        var controller = Controller(_token, h => h.Request.Headers.IfNoneMatch = "\"deadbeef\"");

        Assert.IsType<OkObjectResult>(controller.Catalog());
    }

    [Fact]
    public void Catalog_RevocationIsImmediate()
    {
        Assert.IsType<OkObjectResult>(Controller(_token).Catalog());

        _config.Current.Shares.Clear();

        Assert.Equal(401, Assert.IsAssignableFrom<ObjectResult>(Controller(_token).Catalog()).StatusCode);
    }

    [Fact]
    public void Image_NoToken_Returns401()
    {
        var result = Controller(null).Image(MovieId.ToString("N"), "Primary");

        Assert.Equal(401, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public void Image_UnknownImageType_Returns400()
    {
        var result = Controller(_token).Image(MovieId.ToString("N"), "Banner");

        Assert.Equal(400, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public void Image_BadItemId_Returns404()
    {
        var result = Controller(_token).Image("../../etc/passwd", "Primary");

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public void Image_ItemOutsideCatalog_Returns404_EvenIfImageExists()
    {
        var file = Path.Combine(_tempDir, "poster.jpg");
        File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
        _source.Images[(OutsideId, "Primary")] = file;

        var result = Controller(_token).Image(OutsideId.ToString("N"), "Primary");

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public void Image_ItemWithoutThatImage_Returns404()
    {
        var result = Controller(_token).Image(MovieId.ToString("N"), "Logo");

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public void Image_UnreadableFile_Returns404()
    {
        var file = Path.Combine(_tempDir, "locked.jpg");
        File.WriteAllBytes(file, new byte[] { 1 });
        _source.Images[(MovieId, "Primary")] = file;
        using var exclusive = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = Controller(_token).Image(MovieId.ToString("N"), "Primary");

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public void Image_MissingFileOnDisk_Returns404()
    {
        _source.Images[(MovieId, "Primary")] = Path.Combine(_tempDir, "gone.jpg");

        var result = Controller(_token).Image(MovieId.ToString("N"), "Primary");

        Assert.IsType<NotFoundResult>(result);
    }

    [Theory]
    [InlineData("poster.jpg", "image/jpeg")]
    [InlineData("poster.png", "image/png")]
    [InlineData("poster.webp", "image/webp")]
    public void Image_ServesFileWithContentType(string name, string contentType)
    {
        var file = Path.Combine(_tempDir, name);
        File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
        _source.Images[(MovieId, "Primary")] = file;

        var result = Controller(_token).Image(MovieId.ToString("N"), "primary");

        var physical = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(file, physical.FileName);
        Assert.Equal(contentType, physical.ContentType);
    }

    [Fact]
    public void Image_SeriesOfSharedEpisode_IsServed()
    {
        var file = Path.Combine(_tempDir, "backdrop.jpg");
        File.WriteAllBytes(file, new byte[] { 1 });
        _source.Images[(SeriesId, "Backdrop")] = file;

        var result = Controller(_token).Image(SeriesId.ToString("N"), "Backdrop");

        Assert.IsType<PhysicalFileResult>(result);
    }

    [Fact]
    public void Endpoints_AreAnonymousGets()
    {
        var type = typeof(FederationController);

        var catalog = type.GetMethod(nameof(FederationController.Catalog))!;
        Assert.NotNull(catalog.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Equal("catalog", catalog.GetCustomAttribute<HttpGetAttribute>()!.Template);

        var image = type.GetMethod(nameof(FederationController.Image))!;
        Assert.NotNull(image.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Equal("image/{itemId}/{imageType}", image.GetCustomAttribute<HttpGetAttribute>()!.Template);
    }
}
