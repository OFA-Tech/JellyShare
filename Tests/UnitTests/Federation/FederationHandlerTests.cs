using JellyShare.Application.Features.Catalog;
using JellyShare.Application.Features.Federation;
using JellyShare.Domain.Enums;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Library;
using JellyShare.Domain.Models.Protocol;
using JellyShare.Domain.Models.Requests.Federation;
using JellyShare.UnitTests.Fakes;
using Xunit;

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

        Assert.Equal(ProtocolInfo.Version, result.Value.ProtocolVersion);
        Assert.Equal("Alice", result.Value.ServerName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("js1_wrong")]
    public async Task Catalog_WithoutAValidToken_IsUnauthorized(string? token)
    {
        var result = await _handler.Handle(new GetCatalogQuery(token, []), CancellationToken.None);

        Assert.Equal(ErrorType.Unauthorized, result.Error.Type);
        Assert.Equal("unauthorized", result.Error.Description);
    }

    [Fact]
    public async Task Catalog_OfADisabledShare_IsUnauthorized()
    {
        _config.Settings.Shares[0].Enabled = false;

        var result = await _handler.Handle(new GetCatalogQuery(_token, []), CancellationToken.None);

        Assert.Equal(ErrorType.Unauthorized, result.Error.Type);
    }

    [Fact]
    public async Task Catalog_ReturnsTheBody_AndTheQuotedEtag()
    {
        var result = await _handler.Handle(new GetCatalogQuery(_token, []), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.NotModified);
        Assert.Equal("\"" + result.Value.Catalog!.Etag + "\"", result.Value.Etag);
        Assert.Single(result.Value.Catalog.Items);
    }

    [Fact]
    public async Task Catalog_MatchingIfNoneMatch_IsNotModified_WithoutBuildingEntries()
    {
        var first = await _handler.Handle(new GetCatalogQuery(_token, []), CancellationToken.None);
        _built = 0;

        var second = await _handler.Handle(new GetCatalogQuery(_token, ["\"other\", W/" + first.Value.Etag]), CancellationToken.None);

        Assert.True(second.Value.NotModified);
        Assert.Null(second.Value.Catalog);
        Assert.Equal(first.Value.Etag, second.Value.Etag);
        Assert.Equal(0, _built);
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
        => Assert.Equal(matches, GetCatalogQueryHandler.IfNoneMatchContains([header], "\"abc\""));
}
