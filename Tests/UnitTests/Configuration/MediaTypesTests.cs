using JellyShare.Domain.Enums;
using JellyShare.Domain.Models.Admin;
using JellyShare.Domain.Models.Configuration;

namespace JellyShare.UnitTests.Configuration;

public sealed class MediaTypesTests
{
    private static LibraryInfo Library(Guid id, string? collectionType)
        => new(id, "lib", collectionType, Array.Empty<string>(), false);

    [Fact]
    public void All_IsEveryTypeInEnumOrder_AndSupportedIsVideoAndAudio()
    {
        MediaTypes.All.ShouldBe(Enum.GetValues<MediaType>());
        MediaTypes.All.Count.ShouldBe(7);
        MediaTypes.Supported.ShouldBe(new[] { MediaType.Movies, MediaType.Shows, MediaType.Music, MediaType.Audiobooks });
    }

    [Theory]
    [InlineData("Movie", MediaType.Movies)]
    [InlineData("Episode", MediaType.Shows)]
    [InlineData("Audio", MediaType.Music)]
    [InlineData("AudioBook", MediaType.Audiobooks)]
    public void Of_MapsCatalogTypes(string catalogType, MediaType expected)
        => MediaTypes.Of(catalogType).ShouldBe(expected);

    [Fact]
    public void Of_UnknownType_IsNull()
    {
        MediaTypes.Of("Series").ShouldBeNull();
        MediaTypes.Of(string.Empty).ShouldBeNull();
    }

    [Fact]
    public void FromLibrary_MapsCollectionTypes()
    {
        MediaTypes.FromLibrary("movies").ShouldBe(new[] { MediaType.Movies });
        MediaTypes.FromLibrary("tvshows").ShouldBe(new[] { MediaType.Shows });
        MediaTypes.FromLibrary("music").ShouldBe(new[] { MediaType.Music });
        MediaTypes.FromLibrary("books").ShouldBe(new[] { MediaType.Audiobooks });
        MediaTypes.FromLibrary(null).ShouldBe(new[] { MediaType.Movies, MediaType.Shows });
        MediaTypes.FromLibrary("mixed").ShouldBe(new[] { MediaType.Movies, MediaType.Shows });
    }

    [Fact]
    public void EffectiveShare_LegacyShare_UsesLibraryTypes()
    {
        var movies = Guid.NewGuid();
        var shows = Guid.NewGuid();
        var share = new PeerShare { LibraryIds = { movies, shows, Guid.NewGuid() } };
        var libraries = new[] { Library(movies, "movies"), Library(shows, "tvshows"), Library(Guid.NewGuid(), "music") };

        share.UseDefaultTypes.ShouldBeNull();
        MediaTypes.EffectiveShare(share, new JellyShareSettings(), libraries)
            .ShouldBe(new[] { MediaType.Movies, MediaType.Shows }, ignoreOrder: true);
    }

    [Fact]
    public void EffectiveShare_LegacyShareWithMusicAndBooksLibraries_StaysMoviesAndShows()
    {
        var movies = Guid.NewGuid();
        var music = Guid.NewGuid();
        var books = Guid.NewGuid();
        var share = new PeerShare { LibraryIds = { movies, music, books } };
        var libraries = new[] { Library(movies, "movies"), Library(music, "music"), Library(books, "books") };

        MediaTypes.EffectiveShare(share, new JellyShareSettings(), libraries).ShouldBe(new[] { MediaType.Movies });
    }

    [Fact]
    public void ShareView_LegacyShareWithMusicLibrary_ShowsOnlyMoviesAndShows()
    {
        var share = new PeerShare { Name = "old", LibraryIds = { Fakes.FakeLibraries.MoviesId, Fakes.FakeLibraries.MusicId } };

        var view = JellyShare.Application.Features.Admin.AdminRules.ToView(share, 0, new JellyShareSettings(), new Fakes.FakeLibraries().List());

        view.Types.ShouldBe(new[] { MediaType.Movies });
    }

    [Fact]
    public void EffectiveShare_UseDefault_FollowsDefault()
    {
        var cfg = new JellyShareSettings { DefaultShareTypes = new[] { MediaType.Music } };
        var share = new PeerShare { UseDefaultTypes = true, Types = { MediaType.Movies } };

        MediaTypes.EffectiveShare(share, cfg, Array.Empty<LibraryInfo>()).ShouldBe(new[] { MediaType.Music });
    }

    [Fact]
    public void EffectiveShare_Custom_UsesTypes()
    {
        var share = new PeerShare { UseDefaultTypes = false, Types = { MediaType.Shows } };

        MediaTypes.EffectiveShare(share, new JellyShareSettings(), Array.Empty<LibraryInfo>()).ShouldBe(new[] { MediaType.Shows });
    }

    [Fact]
    public void DefaultShare_Null_IsAll()
        => MediaTypes.DefaultShare(new JellyShareSettings()).ShouldBe(MediaTypes.All, ignoreOrder: true);

    [Fact]
    public void Accepted_All_And_Custom()
    {
        MediaTypes.Accepted(new RemotePeer()).ShouldBe(MediaTypes.All, ignoreOrder: true);
        MediaTypes.Accepted(new RemotePeer { AcceptAllTypes = false, AcceptTypes = { MediaType.Movies } })
            .ShouldBe(new[] { MediaType.Movies });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ParseOrLegacy_Absent_IsMoviesAndShows(string? value)
        => MediaTypes.ParseOrLegacy(value).ShouldBe(new[] { MediaType.Movies, MediaType.Shows }, ignoreOrder: true);

    [Fact]
    public void ParseOrLegacy_IgnoresUnknownNames()
        => MediaTypes.ParseOrLegacy("Music,Bogus").ShouldBe(new[] { MediaType.Music });

    [Theory]
    [InlineData("1")]
    [InlineData("0,1,2")]
    [InlineData("99")]
    [InlineData("-1")]
    public void ParseOrLegacy_IgnoresNumbers(string value)
        => MediaTypes.ParseOrLegacy(value).ShouldBeEmpty();

    [Fact]
    public void Format_RoundTripsThroughParse()
    {
        MediaTypes.Format(new[] { MediaType.Shows, MediaType.Movies, MediaType.Shows }).ShouldBe("Movies,Shows");
        MediaTypes.ParseOrLegacy(MediaTypes.Format(MediaTypes.All)).ShouldBe(MediaTypes.All, ignoreOrder: true);
        MediaTypes.ParseOrLegacy(MediaTypes.Format(new[] { MediaType.Ebooks })).ShouldBe(new[] { MediaType.Ebooks });
    }
}
