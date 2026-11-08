using JellyShare.Application.Features.Placeholders;
using JellyShare.Domain.Models.Placeholders;
using JellyShare.UnitTests.Fakes;

namespace JellyShare.UnitTests.Placeholders;

public sealed class TrackMetadataRulesTests
{
    private static readonly TrackMetadata _written = new("Hells Bells", ["AC/DC"], ["AC/DC"], "Back in Black", 1, 1, true);

    [Fact]
    public void Wanted_Audio_TakesTheEntrysFields_AndLocks()
    {
        var item = TestItems.Track("Hells Bells", albumArtist: "AC/DC", album: "Back in Black", disc: 1, track: 1) with { Artists = ["AC/DC", "Guest"] };

        TrackMetadataRules.Wanted(PathNamingTests.Entry(item))
            .ShouldBe(new TrackMetadata("Hells Bells", ["AC/DC", "Guest"], ["AC/DC"], "Back in Black", 1, 1, true), new TrackMetadataComparer());
    }

    [Fact]
    public void Wanted_Audio_WithoutAlbumArtist_UsesFirstArtist_AndWithoutArtists_UsesAlbumArtist()
    {
        var noAlbumArtist = TestItems.Track("Song", albumArtist: null) with { Artists = ["Solo", " "] };
        var wanted = TrackMetadataRules.Wanted(PathNamingTests.Entry(noAlbumArtist));
        wanted.Artists.ShouldBe(["Solo"]);
        wanted.AlbumArtists.ShouldBe(["Solo"]);

        TrackMetadataRules.Wanted(PathNamingTests.Entry(TestItems.Track("Song"))).Artists.ShouldBe(["AC/DC"]);
    }

    [Fact]
    public void Wanted_AudioBook_KeepsItsChapterNumber()
    {
        var wanted = TrackMetadataRules.Wanted(PathNamingTests.Entry(TestItems.Book("Chapter Two") with { TrackNumber = 2 }));

        wanted.Name.ShouldBe("Chapter Two");
        wanted.IndexNumber.ShouldBe(2);
        wanted.Artists.ShouldBeEmpty();
    }

    [Fact]
    public void ToWrite_SameValuesAndLocked_Nothing()
        => TrackMetadataRules.ToWrite(_written with { Artists = ["AC/DC"] }, _written).ShouldBeNull();

    [Fact]
    public void ToWrite_FreshPlaceholder_WritesEverything()
    {
        // What Jellyfin makes of a .strm: the file stem as name, nothing else.
        var scanned = new TrackMetadata("01 Hells Bells", [], [], null, null, null, false);

        TrackMetadataRules.ToWrite(scanned, _written).ShouldBe(_written);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("artists")]
    [InlineData("albumArtists")]
    [InlineData("album")]
    [InlineData("track")]
    [InlineData("disc")]
    [InlineData("lock")]
    public void ToWrite_AnyFieldDiffers_Writes(string field)
    {
        var current = field switch
        {
            "name" => _written with { Name = "Hells Bells (Live)" },
            "artists" => _written with { Artists = ["AC/DC", "Guest"] },
            "albumArtists" => _written with { AlbumArtists = [] },
            "album" => _written with { Album = "Other" },
            "track" => _written with { IndexNumber = 2 },
            "disc" => _written with { ParentIndexNumber = null },
            _ => _written with { Locked = false },
        };

        TrackMetadataRules.ToWrite(current, _written).ShouldBe(_written);
    }

    private sealed class TrackMetadataComparer : IEqualityComparer<TrackMetadata>
    {
        public bool Equals(TrackMetadata? x, TrackMetadata? y) => x is not null && y is not null && TrackMetadataRules.Same(x, y);

        public int GetHashCode(TrackMetadata obj) => obj.Name.GetHashCode(StringComparison.Ordinal);
    }
}
