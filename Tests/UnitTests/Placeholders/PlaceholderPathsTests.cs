using JellyShare.Application.Features.Placeholders;
using JellyShare.Domain.Models.Configuration;

namespace JellyShare.UnitTests.Placeholders;

public sealed class PlaceholderPathsTests
{
    [Fact]
    public void Strm_ContainsNoTokenAndEscapesKey()
    {
        PlaceholderPaths.StrmContent("http://127.0.0.1:8096/jellyfin", "movie:tmdb:45745").ShouldBe("http://127.0.0.1:8096/jellyfin/JellyShare/v1/play/movie%3Atmdb%3A45745");
        PlaceholderPaths.StrmContent("http://127.0.0.1:8096/jellyfin/", "movie:tmdb:45745").ShouldBe("http://127.0.0.1:8096/jellyfin/JellyShare/v1/play/movie%3Atmdb%3A45745");
    }

    [Theory]
    [InlineData("/data/Movies/Sintel (2010)/Sintel (2010).strm", true)]
    [InlineData("/data/Movies", true)]
    [InlineData("/data/Movies/", true)]
    [InlineData("/data/movies/x.strm", true)]
    [InlineData("/data/Movies2/x.strm", false)]
    [InlineData("/data/Other/x.strm", false)]
    [InlineData("/data/Shows/A/S1.strm", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsPlaceholder_MatchesInsideConfiguredRoots(string? path, bool expected)
    {
        var cfg = new JellyShareSettings { MoviesPlaceholderPath = "/data/Movies", ShowsPlaceholderPath = "/data/Shows/" };
        PlaceholderPaths.IsPlaceholder(path, cfg).ShouldBe(expected);
    }

    [Fact]
    public void IsPlaceholder_NormalizesWindowsSeparators()
    {
        var cfg = new JellyShareSettings { MoviesPlaceholderPath = @"D:\Media\JellyShare\Movies" };
        PlaceholderPaths.IsPlaceholder(@"d:\media\jellyshare\movies\X\X.strm", cfg).ShouldBeTrue();
        PlaceholderPaths.IsPlaceholder(@"D:\Media\JellyShare\Movies2\X.strm", cfg).ShouldBeFalse();
    }

    [Fact]
    public void IsPlaceholder_EmptyRootsNeverMatch()
    {
        var cfg = new JellyShareSettings();
        PlaceholderPaths.IsPlaceholder("/anything", cfg).ShouldBeFalse();
        PlaceholderPaths.IsPlaceholder(string.Empty, cfg).ShouldBeFalse();
    }
}
