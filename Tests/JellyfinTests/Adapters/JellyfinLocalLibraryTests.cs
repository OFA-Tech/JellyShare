using JellyShare.Jellyfin.Adapters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;

namespace JellyShare.JellyfinTests.Adapters;

public sealed class JellyfinLocalLibraryTests
{
    [Fact]
    public void ToSnapshot_MapsTracksAndAudiobooks()
    {
        var track = new Audio
        {
            Path = "/media/Music/Artist A/Album One/01.flac",
            Name = "Song One",
            Album = "Album One",
            AlbumArtists = ["Artist A", "Other"],
            Artists = ["Artist A", "Guest"],
            IndexNumber = 3,
            ParentIndexNumber = 2,
        };
        track.ProviderIds["MusicBrainzRecording"] = "b9f3a1c2-5d4e-4f6a-8b7c-0123456789ab";
        var book = new AudioBook { Path = "/media/Books/Author X/Book Y/01.mp3", Name = "Chapter One" };
        var movie = new Movie { Path = "/media/Movies/a.mkv" };

        var snapshot = JellyfinLocalLibrary.ToSnapshot([], new BaseItem[] { track, book, movie }, ["/media/Music", "/media/Books"]);

        var audio = snapshot.Media.Single(m => m.Type == "Audio");
        audio.Name.ShouldBe("Song One");
        audio.Album.ShouldBe("Album One");
        audio.AlbumArtist.ShouldBe("Artist A");
        audio.Artists.ShouldBe(["Artist A", "Guest"]);
        audio.TrackNumber.ShouldBe(3);
        audio.DiscNumber.ShouldBe(2);
        audio.ProviderIds!["MusicBrainzRecording"].ShouldBe("b9f3a1c2-5d4e-4f6a-8b7c-0123456789ab");

        var chapter = snapshot.Media.Single(m => m.Type == "AudioBook");
        chapter.Name.ShouldBe("Chapter One");
        chapter.FolderPath.ShouldBe("Author X/Book Y");

        snapshot.Media.Single(m => m.Type == "Movie").Path.ShouldBe("/media/Movies/a.mkv");
    }

    [Fact]
    public void ToSnapshot_TrackWithoutAlbumArtist_HasNone()
    {
        var track = new Audio { Path = "/media/Music/x.mp3", Name = "X", Artists = ["Solo"] };

        var audio = JellyfinLocalLibrary.ToSnapshot([], new BaseItem[] { track }, []).Media.ShouldHaveSingleItem();

        audio.AlbumArtist.ShouldBeNull();
        audio.Artists.ShouldBe(["Solo"]);
    }
}
