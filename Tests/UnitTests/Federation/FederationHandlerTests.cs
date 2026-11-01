using JellyShare.Application.Features.Catalog;
using JellyShare.Application.Features.Federation;
using JellyShare.Domain.Enums;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Library;
using JellyShare.Domain.Models.Protocol;
using JellyShare.Domain.Models.Requests.Federation;
using JellyShare.UnitTests.Fakes;

namespace JellyShare.UnitTests.Federation;

public sealed class FederationHandlerTests
{
    private static readonly Guid Lib = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid MovieId = Guid.Parse("00000001-0000-0000-0000-000000000001");

    private readonly FakeSource _source = new();
    private readonly FakeConfig _config = new();
    private readonly ShareTokenService _tokens = new();
    private readonly string _token;
    private readonly GetCatalogQueryHandler _handler;
    private int _built;

    public FederationHandlerTests()
    {
        var (token, hash) = _tokens.Create();
        _token = token;
        _config.Settings.Shares.Add(new PeerShare { Id = Guid.NewGuid(), Name = "bob", TokenHash = hash, Enabled = true, LibraryIds = [Lib] });
        _source.Items.Add((Lib, new LocalItem(MovieId, "Movie", "/media/A.mkv", null, () =>
        {
            _built++;
            return TestItems.Movie("A", tmdb: "1", id: MovieId.ToString("N"));
        })
        { DateModified = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc) }));
        _handler = new GetCatalogQueryHandler(_tokens, _config, new CatalogBuilder(_source, new FakeIdentity(), _config, _ => true));
    }

    [Fact]
    public async Task Ping_ReturnsTheProtocolVersionAndServerName()
    {
        var result = await new PingQueryHandler(new FakeIdentity()).Handle(new PingQuery(), CancellationToken.None);

        result.Value.ProtocolVersion.ShouldBe(ProtocolInfo.Version);
        result.Value.ServerName.ShouldBe("Alice");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("js1_wrong")]
    public async Task Catalog_WithoutAValidToken_IsUnauthorized(string? token)
    {
        var result = await _handler.Handle(new GetCatalogQuery(token, []), CancellationToken.None);

        result.Error.Type.ShouldBe(ErrorType.Unauthorized);
        result.Error.Description.ShouldBe("unauthorized");
    }

    [Fact]
    public async Task Catalog_OfADisabledShare_IsUnauthorized()
    {
        _config.Settings.Shares[0].Enabled = false;

        var result = await _handler.Handle(new GetCatalogQuery(_token, []), CancellationToken.None);

        result.Error.Type.ShouldBe(ErrorType.Unauthorized);
    }

    [Fact]
    public async Task Catalog_ReturnsTheBody_AndTheQuotedEtag()
    {
        var result = await _handler.Handle(new GetCatalogQuery(_token, []), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.NotModified.ShouldBeFalse();
        result.Value.Etag.ShouldBe("\"" + result.Value.Catalog!.Etag + "\"");
        result.Value.Catalog.Items.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Catalog_MatchingIfNoneMatch_IsNotModified_WithoutBuildingEntries()
    {
        var first = await _handler.Handle(new GetCatalogQuery(_token, []), CancellationToken.None);
        _built = 0;

        var second = await _handler.Handle(new GetCatalogQuery(_token, ["\"other\", W/" + first.Value.Etag]), CancellationToken.None);

        second.Value.NotModified.ShouldBeTrue();
        second.Value.Catalog.ShouldBeNull();
        second.Value.Etag.ShouldBe(first.Value.Etag);
        _built.ShouldBe(0);
    }

    [Theory]
    [InlineData("*", true)]
    [InlineData("\"abc\"", true)]
    [InlineData("W/\"abc\"", true)]
    [InlineData("\"x\" , \"abc\"", true)]
    [InlineData("\"x\"", false)]
    [InlineData("abc", false)]
    [InlineData(null, false)]
    public void IfNoneMatch_Rules(string? header, bool matches)
        => GetCatalogQueryHandler.IfNoneMatchContains([header], "\"abc\"").ShouldBe(matches);
}
