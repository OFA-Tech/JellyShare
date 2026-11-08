using JellyShare.Domain.Keys;
using JellyShare.Domain.Models.Protocol;
using static JellyShare.UnitTests.Fakes.TestItems;

namespace JellyShare.UnitTests.Keys;

public sealed class TitleKeyTests
{
    private static MergedEntry EntryOf(CatalogItem item)
        => MergeIndex.Build(new[] { (PeerA, item) }).Entries.ShouldHaveSingleItem();

    [Fact]
    public void TitleKey_TracksOfOneAlbum_AreOneTitle()
    {
        var one = EntryOf(Track("Hells Bells", track: 1));
        var two = EntryOf(Track("Shoot to Thrill", track: 2, albumArtist: "ac dc", album: "BACK IN BLACK"));

        one.CanonicalKey.ShouldNotBe(two.CanonicalKey);
        TitleKey.For(one).ShouldStartWith("album:name:");
        TitleKey.For(one).ShouldBe(TitleKey.For(two));
    }

    [Fact]
    public void TitleKey_OtherAlbum_IsAnotherTitle()
        => TitleKey.For(EntryOf(Track("A"))).ShouldNotBe(TitleKey.For(EntryOf(Track("A", album: "Highway to Hell"))));

    [Fact]
    public void TitleKey_TrackWithoutAlbum_IsItsCanonicalKey()
    {
        var entry = EntryOf(Track("Single", album: null));

        TitleKey.For(entry).ShouldBe(entry.CanonicalKey);
    }

    [Fact]
    public void TitleKey_SymbolOnlyAlbum_TracksShareOneKey_OtherSymbolAlbumDiffers()
    {
        var one = TitleKey.ForAudio(Track("Eraser", track: 1, albumArtist: "Ed Sheeran", album: "÷"));
        var two = TitleKey.ForAudio(Track("Castle on the Hill", track: 2, albumArtist: "ed sheeran", album: " ÷ "));

        one.ShouldNotBeNull().ShouldStartWith("album:name:");
        two.ShouldBe(one);
        TitleKey.ForAudio(Track("Eraser", albumArtist: "Ed Sheeran", album: "×")).ShouldNotBe(one);
    }

    [Fact]
    public void TitleKey_SymbolOnlyAlbumArtist_IsTheAlbumArtist()
    {
        var item = Track("Heart of Hearts", albumArtist: "!!!", album: "Thr!!!er");

        TitleKey.AlbumArtistOf(item).ShouldBe("!!!");
        TitleKey.ForAudio(item).ShouldNotBe(TitleKey.ForAudio(item with { AlbumArtist = "  " }));
    }

    [Fact]
    public void TitleKey_FilesOfOneAudioBook_AreOneTitle()
    {
        var one = EntryOf(Book("Part 1"));
        var two = EntryOf(Book("Part 2"));

        TitleKey.For(one).ShouldStartWith("book:");
        TitleKey.For(one).ShouldBe(TitleKey.For(two));
        TitleKey.For(one).ShouldNotBe(TitleKey.For(EntryOf(Book("Part 1", folderPath: "Author X/Other"))));
    }

    [Fact]
    public void TitleKey_Movie_IsCanonicalKey()
    {
        var entry = EntryOf(Movie("Sintel", tmdb: "45745"));

        TitleKey.For(entry).ShouldBe("movie:tmdb:45745");
    }

    [Fact]
    public void TitleKey_Episode_IsCanonicalKey()
    {
        var entry = EntryOf(Episode("Show", new() { ["Tvdb"] = "1" }, 1, 2));

        TitleKey.For(entry).ShouldBe("ep:tvdb:1:s1e2");
    }
}
