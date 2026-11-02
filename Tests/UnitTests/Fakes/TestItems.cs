using JellyShare.Domain.Models.Protocol;

namespace JellyShare.UnitTests.Fakes;

internal static class TestItems
{
    public static readonly Guid PeerA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    public static readonly Guid PeerB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    public static readonly Guid PeerC = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    private static readonly MediaInfo _emptyMedia = new(null, null, null, null, null, [], []);

    public static CatalogItem Movie(string name, string? tmdb = null, string? imdb = null, string id = "m1")
    {
        var ids = new Dictionary<string, string>();
        if (tmdb is not null)
        {
            ids["Tmdb"] = tmdb;
        }

        if (imdb is not null)
        {
            ids["Imdb"] = imdb;
        }

        return new CatalogItem(
            id, "Movie", name, 2020, ids, null, null, null, null, null, null, _emptyMedia, [], DateTime.UnixEpoch);
    }

    public static CatalogItem Episode(
        string series,
        Dictionary<string, string>? seriesIds,
        int? season,
        int? episode,
        string id = "e1")
        => new(
            id, "Episode", "Ep", 2020, new Dictionary<string, string>(), series, seriesIds, season, episode,
            null, null, _emptyMedia, [], DateTime.UnixEpoch, "s1");
}
