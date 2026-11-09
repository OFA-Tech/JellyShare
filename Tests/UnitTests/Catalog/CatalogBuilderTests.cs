using System.Text.Json;
using JellyShare.Application.Features.Catalog;
using JellyShare.Domain.Enums;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Library;
using JellyShare.Domain.Models.Protocol;
using JellyShare.UnitTests.Fakes;

namespace JellyShare.UnitTests.Catalog;

public sealed class CatalogBuilderTests
{
    private const string MoviesRoot = "/data/placeholders/Movies";
    private const string ShowsRoot = "/data/placeholders/Shows";

    private static readonly Guid _libA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly DateTime _modified = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private static CatalogItem Item(Guid id, string type = "Movie", DateTime? modified = null, string? seriesId = null)
        => new(
            id.ToString("N"),
            type,
            "Name " + id.ToString("N")[..4],
            2010,
            new Dictionary<string, string> { ["Tmdb"] = "1" },
            type == "Episode" ? "Show" : null,
            type == "Episode" ? new Dictionary<string, string> { ["Tvdb"] = "9" } : null,
            type == "Episode" ? 1 : null,
            type == "Episode" ? 2 : null,
            1000,
            null,
            new MediaInfo("mkv", "h264", 1920, 1080, 4000000, Array.Empty<AudioTrack>(), Array.Empty<SubtitleTrack>()),
            new[] { "Primary" },
            modified ?? _modified,
            seriesId);

    private int _materialized;

    private LocalItem Local(Guid id, string? path, string type = "Movie", DateTime? modified = null, string? seriesId = null)
        => new(
            id,
            type,
            path,
            seriesId is null ? null : Guid.Parse(seriesId),
            () =>
            {
                Interlocked.Increment(ref _materialized);
                return Item(id, type, modified, seriesId);
            })
        {
            DateModified = modified ?? _modified,
        };

    private static Guid G(int n) => new(n, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1);

    private static IReadOnlySet<MediaType> Requested { get; } = new HashSet<MediaType> { MediaType.Movies, MediaType.Shows };

    private static PeerShare Share(params MediaType[] types)
        => new()
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Name = "bob",
            Enabled = true,
            UseDefaultTypes = false,
            Types = (types.Length == 0 ? [MediaType.Movies, MediaType.Shows] : types).ToList(),
        };

    private static PeerShare NoTypes() => new() { Id = Guid.NewGuid(), Name = "none", Enabled = true, UseDefaultTypes = false };

    private static CatalogBuilder Builder(FakeSource source, Func<string, bool>? exists = null, FakeLibraries? libraries = null, FakeConfig? config = null)
        => new(source, new FakeIdentity(), config ?? new FakeConfig(), libraries ?? new FakeLibraries(), exists ?? (_ => true));

    [Fact]
    public void Build_ExcludesPlaceholderPaths()
    {
        var source = new FakeSource();
        source.Items.Add((_libA, Local(G(1), "/media/Movies/A/A.mkv")));
        source.Items.Add((_libA, Local(G(2), MoviesRoot + "/B/B.mkv")));
        source.Items.Add((_libA, Local(G(3), ShowsRoot + "/S/Season 01/S.mkv", "Episode")));

        var catalog = Builder(source).Build(Share(), Requested);

        catalog.Items.Select(i => i.Id).ShouldBe(new[] { G(1).ToString("N") });
    }

    [Fact]
    public void Build_ExcludesStrmFiles()
    {
        var source = new FakeSource();
        source.Items.Add((_libA, Local(G(1), "/media/Movies/A/A.mkv")));
        source.Items.Add((_libA, Local(G(2), "/media/Movies/B/B.STRM")));
        source.Items.Add((_libA, Local(G(3), "/media/Movies/C/C.strm")));

        var catalog = Builder(source).Build(Share(), Requested);

        catalog.Items.Select(i => i.Id).ShouldBe(new[] { G(1).ToString("N") });
    }

    [Fact]
    public void Build_ExcludesNullOrEmptyPath()
    {
        var source = new FakeSource();
        source.Items.Add((_libA, Local(G(1), "/media/Movies/A/A.mkv")));
        source.Items.Add((_libA, Local(G(2), null)));
        source.Items.Add((_libA, Local(G(3), string.Empty)));

        var catalog = Builder(source).Build(Share(), Requested);

        catalog.Items.ShouldHaveSingleItem();
    }

    [Fact]
    public void Build_ExcludesMissingFiles()
    {
        var source = new FakeSource();
        source.Items.Add((_libA, Local(G(1), "/media/Movies/A/A.mkv")));
        source.Items.Add((_libA, Local(G(2), "/media/Movies/B/B.mkv")));

        var catalog = Builder(source, p => !p.Contains("/B/")).Build(Share(), Requested);

        catalog.Items.Select(i => i.Id).ShouldBe(new[] { G(1).ToString("N") });
    }

    [Fact]
    public void Build_NoTypes_ReturnsEmptyCatalog_WithoutReadingTheLibrary()
    {
        var source = new FakeSource();
        source.Items.Add((_libA, Local(G(1), "/media/Movies/A/A.mkv")));

        var catalog = Builder(source).Build(NoTypes(), Requested);

        catalog.Items.ShouldBeEmpty();
        catalog.ProtocolVersion.ShouldBe("1.0");
        catalog.ServerName.ShouldBe("Alice");
        source.GetItemsCalls.ShouldBe(0);
    }

    [Fact]
    public void Catalog_WithoutTypes_ReturnsOnlyMoviesAndShows()
    {
        var source = new FakeSource();
        source.Items.Add((_libA, Local(G(1), "/media/Movies/A/A.mkv")));
        source.Items.Add((_libA, Local(G(2), "/media/Music/A/a.flac", "Audio")));

        var catalog = Builder(source).Build(Share(MediaType.Movies, MediaType.Shows, MediaType.Music), MediaTypes.ParseOrLegacy(null));

        catalog.Items.Select(i => i.Id).ShouldBe(new[] { G(1).ToString("N") });
    }

    [Fact]
    public void Catalog_ServesIntersectionOfShareAndRequest()
    {
        var source = new FakeSource();
        source.Items.Add((_libA, Local(G(1), "/media/Movies/A/A.mkv")));
        source.Items.Add((_libA, Local(G(2), "/media/Shows/S/Season 01/S.mkv", "Episode", seriesId: G(99).ToString("N"))));

        var catalog = Builder(source).Build(Share(MediaType.Movies), Requested);

        catalog.Items.Select(i => i.Id).ShouldBe(new[] { G(1).ToString("N") });
    }

    [Fact]
    public void Catalog_EtagDiffersByServedTypes()
    {
        var source = new FakeSource();
        source.Items.Add((_libA, Local(G(1), "/media/Movies/A/A.mkv")));
        var builder = Builder(source);
        var share = Share(MediaType.Movies, MediaType.Shows);

        var both = builder.Build(share, Requested).Etag;
        var moviesOnly = builder.Build(share, new HashSet<MediaType> { MediaType.Movies }).Etag;
        var none = builder.Build(share, new HashSet<MediaType> { MediaType.Music }).Etag;

        moviesOnly.ShouldNotBe(both);
        none.ShouldNotBe(both);
        none.ShouldNotBe(moviesOnly);
        builder.Build(share, Requested).Etag.ShouldBe(both);
    }

    [Fact]
    public void Catalog_LegacyShare_ServesWhatItServedBefore()
    {
        var source = new FakeSource();
        source.Items.Add((_libA, Local(G(1), "/media/Movies/A/A.mkv")));
        source.Items.Add((_libA, Local(G(2), "/media/Shows/S/Season 01/S.mkv", "Episode", seriesId: G(99).ToString("N"))));
        var libraries = new FakeLibraries();
        libraries.Libraries.Add(new(_libA, "Mixed", null, ["/media"], false));
        var legacy = new PeerShare { Id = Guid.NewGuid(), Name = "old", Enabled = true, LibraryIds = [_libA] };
        var onlyMovies = new PeerShare { Id = Guid.NewGuid(), Name = "old", Enabled = true, LibraryIds = [FakeLibraries.MoviesId] };
        var builder = Builder(source, libraries: libraries);

        builder.Build(legacy, Requested).Items.Count.ShouldBe(2);
        builder.Build(onlyMovies, Requested).Items.ShouldHaveSingleItem().Type.ShouldBe("Movie");
    }

    [Fact]
    public void IsExposed_LegacyShare_ReadsLibrariesOncePer30Seconds()
    {
        var source = new FakeSource();
        source.Items.Add((FakeLibraries.MoviesId, Local(G(1), "/media/Movies/A/A.mkv")));
        var libraries = new FakeLibraries();
        var clock = new MutableClock();
        var builder = new CatalogBuilder(source, new FakeIdentity(), new FakeConfig(), libraries, _ => true, clock);
        var legacy = new PeerShare { Id = Guid.NewGuid(), Name = "old", Enabled = true, LibraryIds = [FakeLibraries.MoviesId] };

        builder.IsExposed(legacy, G(1)).ShouldBeTrue();
        clock.Now += TimeSpan.FromSeconds(29);
        builder.IsExposed(legacy, G(1)).ShouldBeTrue();
        libraries.ListCalls.ShouldBe(1);

        clock.Now += TimeSpan.FromSeconds(2);
        builder.IsExposed(legacy, G(1)).ShouldBeTrue();
        libraries.ListCalls.ShouldBe(2);
    }

    [Fact]
    public void Catalog_ShareFollowingTheDefault_ServesTheConfiguredDefault()
    {
        var source = new FakeSource();
        source.Items.Add((_libA, Local(G(1), "/media/Movies/A/A.mkv")));
        source.Items.Add((_libA, Local(G(2), "/media/Shows/S/Season 01/S.mkv", "Episode", seriesId: G(99).ToString("N"))));
        var config = new FakeConfig();
        config.Settings.DefaultShareTypes = [MediaType.Shows];
        var share = new PeerShare { Id = Guid.NewGuid(), Name = "bob", Enabled = true, UseDefaultTypes = true };

        var catalog = Builder(source, config: config).Build(share, Requested);

        catalog.Items.ShouldHaveSingleItem().Type.ShouldBe("Episode");
    }

    [Fact]
    public void Build_EpisodeCarriesSeriesNameProviderIdsAndSeriesId()
    {
        var seriesId = G(99).ToString("N");
        var source = new FakeSource();
        source.Items.Add((_libA, Local(G(1), "/media/Shows/S/Season 01/S.mkv", "Episode", seriesId: seriesId)));

        var item = Builder(source).Build(Share(), Requested).Items.ShouldHaveSingleItem();

        item.Type.ShouldBe("Episode");
        item.SeriesName.ShouldBe("Show");
        item.SeriesProviderIds!["Tvdb"].ShouldBe("9");
        item.SeriesId.ShouldBe(seriesId);
    }

    [Fact]
    public void Build_EtagStableForSameInput()
    {
        var source = new FakeSource();
        source.Items.Add((_libA, Local(G(1), "/media/Movies/A/A.mkv")));
        source.Items.Add((_libA, Local(G(2), "/media/Movies/B/B.mkv")));

        var builder = Builder(source);
        var first = builder.Build(Share(), Requested).Etag;
        source.Items.Reverse();
        var second = builder.Build(Share(), Requested).Etag;

        second.ShouldBe(first);
        first.ShouldMatch("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Build_EtagChangesWhenItemModified()
    {
        var source = new FakeSource();
        source.Items.Add((_libA, Local(G(1), "/media/Movies/A/A.mkv")));
        var before = Builder(source).Build(Share(), Requested).Etag;

        source.Items.Clear();
        source.Items.Add((_libA, Local(G(1), "/media/Movies/A/A.mkv", modified: _modified.AddMinutes(1))));
        var after = Builder(source).Build(Share(), Requested).Etag;

        after.ShouldNotBe(before);
    }

    [Fact]
    public void Build_EtagIgnoresExcludedItems()
    {
        var source = new FakeSource();
        source.Items.Add((_libA, Local(G(1), "/media/Movies/A/A.mkv")));
        var before = Builder(source).Build(Share(), Requested).Etag;

        source.Items.Add((_libA, Local(G(2), MoviesRoot + "/B/B.mkv")));
        var after = Builder(source).Build(Share(), Requested).Etag;

        after.ShouldBe(before);
    }

    [Fact]
    public void Build_SerializesToCamelCaseWireShape()
    {
        var source = new FakeSource();
        source.Items.Add((_libA, Local(G(1), "/media/Movies/A/A.mkv")));

        var json = JsonSerializer.Serialize(Builder(source).Build(Share(), Requested));

        json.ShouldContain("\"protocolVersion\":\"1.0\"", Case.Sensitive);
        json.ShouldContain("\"serverName\":\"Alice\"", Case.Sensitive);
        json.ShouldContain("\"etag\":", Case.Sensitive);
        json.ShouldContain("\"items\":[{\"id\":", Case.Sensitive);
    }

    [Fact]
    public void IsExposed_TrueForCatalogItemsAndTheirSeries_FalseOtherwise()
    {
        var seriesId = G(99);
        var source = new FakeSource();
        source.Items.Add((_libA, Local(G(1), "/media/Movies/A/A.mkv")));
        source.Items.Add((_libA, Local(G(2), "/media/Shows/S/Season 01/S.mkv", "Episode", seriesId: seriesId.ToString("N"))));
        var builder = Builder(source);
        var share = Share();

        builder.IsExposed(share, G(1)).ShouldBeTrue();
        builder.IsExposed(share, G(2)).ShouldBeTrue();
        builder.IsExposed(share, seriesId).ShouldBeTrue();
        builder.IsExposed(share, Guid.NewGuid()).ShouldBeFalse();
    }

    [Fact]
    public void IsExposed_ItemOfUnsharedType_False()
    {
        var source = new FakeSource();
        source.Items.Add((_libA, Local(G(1), "/media/Movies/A/A.mkv")));
        source.Items.Add((_libA, Local(G(2), "/media/Shows/S/Season 01/S.mkv", "Episode", seriesId: G(99).ToString("N"))));
        var builder = Builder(source);
        var moviesOnly = Share(MediaType.Movies);

        builder.IsExposed(moviesOnly, G(1)).ShouldBeTrue();
        builder.IsExposed(moviesOnly, G(2)).ShouldBeFalse();
        builder.IsExposed(moviesOnly, G(99)).ShouldBeFalse();
    }

    [Fact]
    public void Build_DoesNotMaterializeExcludedItems()
    {
        var source = new FakeSource();
        source.Items.Add((_libA, Local(G(1), "/media/Movies/A/A.mkv")));
        source.Items.Add((_libA, Local(G(2), MoviesRoot + "/B/B.mkv")));
        source.Items.Add((_libA, Local(G(3), "/media/Movies/C/C.strm")));
        source.Items.Add((_libA, Local(G(4), null)));
        source.Items.Add((_libA, Local(G(5), "/media/Movies/missing/M.mkv")));

        var catalog = Builder(source, p => !p.Contains("/missing/")).Build(Share(), Requested);

        catalog.Items.ShouldHaveSingleItem();
        _materialized.ShouldBe(1);
    }

    [Fact]
    public void IsExposed_NeverMaterializesCatalogItems()
    {
        var source = new FakeSource();
        source.Items.Add((_libA, Local(G(1), "/media/Movies/A/A.mkv")));
        source.Items.Add((_libA, Local(G(2), "/media/Shows/S/Season 01/S.mkv", "Episode", seriesId: G(99).ToString("N"))));

        var builder = Builder(source);

        builder.IsExposed(Share(), G(1)).ShouldBeTrue();
        builder.IsExposed(Share(), G(99)).ShouldBeTrue();
        _materialized.ShouldBe(0);
    }

    [Fact]
    public void IsExposed_SeriesWhoseEpisodesArePlaceholders_IsNotExposed()
    {
        var onlyPlaceholders = G(90);
        var source = new FakeSource();
        source.Items.Add((_libA, Local(G(1), ShowsRoot + "/P/Season 01/P.strm", "Episode", seriesId: onlyPlaceholders.ToString("N"))));
        source.Items.Add((_libA, Local(G(2), ShowsRoot + "/P/Season 01/P2.mkv", "Episode", seriesId: onlyPlaceholders.ToString("N"))));
        var builder = Builder(source);

        builder.IsExposed(Share(), onlyPlaceholders).ShouldBeFalse();
    }

    [Fact]
    public void IsExposed_ChecksOnlyTheRequestedItem_NeverEnumeratesTheLibrary()
    {
        var source = new FakeSource();
        source.Items.Add((_libA, Local(G(1), "/media/Movies/A/A.mkv")));
        source.Items.Add((_libA, Local(G(2), "/media/Movies/missing/B.mkv")));
        source.Items.Add((_libA, Local(G(3), "/media/Shows/S/Season 01/S.mkv", "Episode", seriesId: G(99).ToString("N"))));
        var builder = Builder(source, p => !p.Contains("/missing/"));
        var share = Share();

        builder.IsExposed(share, G(1)).ShouldBeTrue();
        builder.IsExposed(share, G(2)).ShouldBeFalse();
        builder.IsExposed(share, G(99)).ShouldBeTrue();

        source.GetItemsCalls.ShouldBe(0);
        source.CandidateCalls.ShouldBe(3);
    }

    [Fact]
    public void IsExposed_NoTypes_IsFalseWithoutQuery()
    {
        var source = new FakeSource();
        source.Items.Add((_libA, Local(G(1), "/media/Movies/A/A.mkv")));

        Builder(source).IsExposed(NoTypes(), G(1)).ShouldBeFalse();
        source.CandidateCalls.ShouldBe(0);
    }

    [Fact]
    public void Plan_EtagIsComputedWithoutMaterializingItems()
    {
        var source = new FakeSource();
        source.Items.Add((_libA, Local(G(1), "/media/Movies/A/A.mkv")));
        source.Items.Add((_libA, Local(G(2), "/media/Movies/B/B.mkv")));
        var builder = Builder(source);

        var plan = builder.Plan(Share(), Requested);

        plan.Etag.ShouldMatch("^[0-9a-f]{64}$");
        _materialized.ShouldBe(0);
        plan.Build().Etag.ShouldBe(plan.Etag);
        _materialized.ShouldBe(2);
    }

    [Fact]
    public void Build_SameEtagTwice_IsServedFromCache()
    {
        var source = new FakeSource();
        source.Items.Add((_libA, Local(G(1), "/media/Movies/A/A.mkv")));
        source.Items.Add((_libA, Local(G(2), "/media/Movies/B/B.mkv")));
        var builder = Builder(source);

        var first = builder.Build(Share(), Requested);
        var second = builder.Build(Share(), Requested);

        second.ShouldBeSameAs(first);
        _materialized.ShouldBe(2);
    }

    [Fact]
    public void Build_ChangedItem_RebuildsInsteadOfServingCache()
    {
        var source = new FakeSource();
        source.Items.Add((_libA, Local(G(1), "/media/Movies/A/A.mkv")));
        var builder = Builder(source);
        var first = builder.Build(Share(), Requested);

        source.Items.Clear();
        source.Items.Add((_libA, Local(G(1), "/media/Movies/A/A.mkv", modified: _modified.AddMinutes(1))));
        var second = builder.Build(Share(), Requested);

        second.Etag.ShouldNotBe(first.Etag);
        second.Items.ShouldHaveSingleItem().DateModified.ShouldBe(_modified.AddMinutes(1));
    }

    private static LocalItem AudioLocal(Guid id, string type, string path, Guid album, CatalogItem item)
        => new(id, type, path, album, () => item) { DateModified = _modified };

    [Fact]
    public void Catalog_MusicRequestedAndShared_ServesTracksWithAlbumFields()
    {
        var album = G(50);
        var track = new CatalogItem(
            G(1).ToString("N"), "Audio", "Song", 1999, new Dictionary<string, string> { ["MusicBrainzTrack"] = "t-1" },
            null, null, null, null, 2000, null, new MediaInfo("mp3", null, null, null, 320000, [], []), ["Primary"], _modified,
            null, "Album", "Artist", ["Artist", "Guest"], 2, 7, album.ToString("N"));
        var source = new FakeSource();
        source.Items.Add((_libA, AudioLocal(G(1), "Audio", "/media/Music/Artist/Album/07.mp3", album, track)));
        source.Items.Add((_libA, Local(G(2), "/media/Movies/A/A.mkv")));
        var music = new HashSet<MediaType> { MediaType.Music };

        var catalog = Builder(source).Build(Share(MediaType.Music), music);

        var served = catalog.Items.ShouldHaveSingleItem();
        served.Type.ShouldBe("Audio");
        (served.Album, served.AlbumArtist, served.DiscNumber, served.TrackNumber, served.AlbumId).ShouldBe(("Album", "Artist", 2, 7, album.ToString("N")));
        served.Artists.ShouldBe(["Artist", "Guest"]);
        served.ProviderIds["MusicBrainzTrack"].ShouldBe("t-1");
        served.SeasonNumber.ShouldBeNull();
        served.EpisodeNumber.ShouldBeNull();
        JsonSerializer.Serialize(served).ShouldContain("\"trackNumber\":7", Case.Sensitive);
        Builder(source).Build(Share(MediaType.Movies), music).Items.ShouldBeEmpty();
    }

    [Fact]
    public void Catalog_AudiobookItem_CarriesFolderPathWithoutRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "books");
        var file = Path.Combine(root, "Author X", "Book Y", "01.mp3");
        var folderPath = AudiobookFolders.Relative(file, [root]);
        var book = new CatalogItem(
            G(1).ToString("N"), "AudioBook", "Book Y", null, new Dictionary<string, string>(),
            null, null, null, null, 2000, null, new MediaInfo("mp3", null, null, null, null, [], []), [], _modified,
            FolderPath: folderPath);
        var source = new FakeSource();
        source.Items.Add((_libA, AudioLocal(G(1), "AudioBook", file, G(60), book)));

        var served = Builder(source).Build(Share(MediaType.Audiobooks), new HashSet<MediaType> { MediaType.Audiobooks }).Items.ShouldHaveSingleItem();

        served.FolderPath.ShouldBe("Author X/Book Y");
        served.FolderPath!.ShouldNotContain(root, Case.Insensitive);
    }

    [Fact]
    public void AudiobookFolders_FileDirectlyInRootOrOutsideIt_HasNoFolderPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "books");

        AudiobookFolders.Relative(Path.Combine(root, "book.m4b"), [root]).ShouldBeNull();
        AudiobookFolders.Relative(Path.Combine(Path.GetTempPath(), "other", "A", "b.m4b"), [root]).ShouldBeNull();
        AudiobookFolders.Relative(Path.Combine(root, "A", "B", "b.m4b"), [Path.Combine(root, "A"), root]).ShouldBe("B");
        AudiobookFolders.Relative(null, [root]).ShouldBeNull();
    }

    [Fact]
    public void Catalog_LegacyShareWithMusicLibrary_ServesNoAudio_AndExposesNone()
    {
        var album = G(50);
        var source = new FakeSource();
        source.Items.Add((_libA, AudioLocal(G(1), "Audio", "/media/Music/A/01.mp3", album, Item(G(1), "Audio"))));
        source.Items.Add((_libA, Local(G(2), "/media/Movies/A/A.mkv")));
        var legacy = new PeerShare
        {
            Id = Guid.NewGuid(),
            Name = "old",
            Enabled = true,
            LibraryIds = { FakeLibraries.MoviesId, FakeLibraries.MusicId },
        };
        var builder = Builder(source);

        builder.Build(legacy, new HashSet<MediaType> { MediaType.Music }).Items.ShouldBeEmpty();
        builder.Build(legacy, new HashSet<MediaType> { MediaType.Movies, MediaType.Music }).Items.ShouldHaveSingleItem().Type.ShouldBe("Movie");
        builder.IsExposed(legacy, G(1)).ShouldBeFalse();
        builder.IsExposed(legacy, album).ShouldBeFalse();
    }

    [Fact]
    public void IsExposed_AlbumId_ResolvesToItsTracks()
    {
        var album = G(50);
        var source = new FakeSource();
        source.Items.Add((_libA, AudioLocal(G(1), "Audio", "/media/Music/A/01.mp3", album, Item(G(1), "Audio"))));

        var builder = Builder(source);

        builder.IsExposed(Share(MediaType.Music), album).ShouldBeTrue();
        builder.IsExposed(Share(MediaType.Movies), album).ShouldBeFalse();
    }
}
