using JellyShare.Jellyfin.Configuration;
using JellyShare.Jellyfin.Placeholders;

using Xunit;

namespace JellyShare.JellyfinTests.Placeholders;

public class PlaceholderPathsTests
{
    [Fact]
    public void Strm_ContainsNoTokenAndEscapesKey()
    {
        Assert.Equal(
            "http://127.0.0.1:8096/jellyfin/JellyShare/v1/play/movie%3Atmdb%3A45745",
            PlaceholderPaths.StrmContent("http://127.0.0.1:8096/jellyfin", "movie:tmdb:45745"));
        Assert.Equal(
            "http://127.0.0.1:8096/jellyfin/JellyShare/v1/play/movie%3Atmdb%3A45745",
            PlaceholderPaths.StrmContent("http://127.0.0.1:8096/jellyfin/", "movie:tmdb:45745"));
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
        var cfg = new PluginConfiguration { MoviesPlaceholderPath = "/data/Movies", ShowsPlaceholderPath = "/data/Shows/" };
        Assert.Equal(expected, PlaceholderPaths.IsPlaceholder(path, cfg));
    }

    [Fact]
    public void IsPlaceholder_NormalizesWindowsSeparators()
    {
        var cfg = new PluginConfiguration { MoviesPlaceholderPath = @"D:\Media\JellyShare\Movies" };
        Assert.True(PlaceholderPaths.IsPlaceholder(@"d:\media\jellyshare\movies\X\X.strm", cfg));
        Assert.False(PlaceholderPaths.IsPlaceholder(@"D:\Media\JellyShare\Movies2\X.strm", cfg));
    }

    [Fact]
    public void IsPlaceholder_EmptyRootsNeverMatch()
    {
        var cfg = new PluginConfiguration();
        Assert.False(PlaceholderPaths.IsPlaceholder("/anything", cfg));
        Assert.False(PlaceholderPaths.IsPlaceholder(string.Empty, cfg));
    }
}
