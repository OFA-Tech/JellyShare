using System.Text.Json;
using JellyShare.Application.Features.Placeholders;
using JellyShare.Domain.Interfaces.Relay;
using JellyShare.Domain.Models.Protocol;
using JellyShare.Jellyfin.Adapters;
using JellyShare.Jellyfin.Filters;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.DependencyInjection;

namespace JellyShare.JellyfinTests.Adapters;

public sealed class AudioRelaySourceProviderTests
{
    private const string Key = "track:ac/dc|back in black|1|1";
    private static readonly Guid _trackId = Guid.Parse("44444444-0000-0000-0000-000000000004");

    private readonly Lookup _lookup = new();

    private static CatalogItem Track(long? runTimeTicks = 2_000_000_000)
        => TestItems.Track("Hells Bells") with
        {
            RunTimeTicks = runTimeTicks,
            Media = new Domain.Models.Protocol.MediaInfo("flac", null, null, null, 900_000, [new AudioTrack(null, "flac", 2, null)], []),
        };

    private AudioRelaySourceProvider Provider()
        => new(new ServiceCollection()
            .AddSingleton<IPlaceholderLookup>(_lookup)
            .AddSingleton<IRelayBase>(new FixedRelay())
            .BuildServiceProvider());

    private static Audio Item(long? runTimeTicks = null)
        => new() { Id = _trackId, Name = "Hells Bells", Path = "/jellyshare/Music/AC-DC/Back in Black/01 Hells Bells.strm", RunTimeTicks = runTimeTicks };

    [Fact]
    public async Task AudioPlaceholder_ReturnsOneRelaySource()
    {
        _lookup.Items[_trackId] = new PlaceholderMedia(Key, 900_000, "album", Track());

        var sources = (await Provider().GetMediaSources(Item(), CancellationToken.None)).ToList();

        var source = sources.ShouldHaveSingleItem();
        source.Id.ShouldBe(AudioRelaySourceProvider.SourceId(_trackId));
        source.Id.ShouldNotBe(_trackId.ToString("N"));
        source.Path.ShouldBe(PlaceholderPaths.StrmContent(new FixedRelay().Current, Key));
        source.Protocol.ShouldBe(MediaProtocol.Http);
        source.IsRemote.ShouldBeTrue();
        source.Container.ShouldBe("flac");
        source.Bitrate.ShouldBe(900_000);
        source.RequiresOpening.ShouldBeFalse();
        source.SupportsDirectStream.ShouldBeTrue();
        source.SupportsTranscoding.ShouldBeTrue();
    }

    [Fact]
    public void SourceId_IsStable()
        => AudioRelaySourceProvider.SourceId(_trackId).ShouldBe(AudioRelaySourceProvider.SourceId(_trackId));

    [Fact]
    public void Source_StoredStreamsAndRuntime_AreUsed()
    {
        var stored = new List<MediaStream> { new() { Type = MediaStreamType.Audio, Index = 0, Codec = "mp3", Channels = 1 } };

        var source = AudioRelaySourceProvider.Source(Item(runTimeTicks: 5), new PlaceholderMedia(Key, 900_000, null, Track()), "http://127.0.0.1:8096", stored);

        source.MediaStreams.ShouldHaveSingleItem().Codec.ShouldBe("mp3");
        source.RunTimeTicks.ShouldBe(5);
    }

    [Fact]
    public void Source_NothingStored_SynthesizesStreamAndRuntimeFromCatalog()
    {
        var source = AudioRelaySourceProvider.Source(Item(), new PlaceholderMedia(Key, 900_000, null, Track()), "http://127.0.0.1:8096", []);

        var stream = source.MediaStreams.ShouldHaveSingleItem();
        stream.Type.ShouldBe(MediaStreamType.Audio);
        stream.Codec.ShouldBe("flac");
        stream.Channels.ShouldBe(2);
        stream.IsDefault.ShouldBeTrue();
        source.RunTimeTicks.ShouldBe(2_000_000_000);
    }

    [Fact]
    public void Source_NoContainerAnywhere_UsesTheAudioCodec()
    {
        // Jellyfin stores no container for audiobooks of a multi-file folder; without one, universal transcodes to HLS.
        var book = Track() with { Media = new Domain.Models.Protocol.MediaInfo(null, null, null, null, 64_000, [new AudioTrack(null, "mp3", 1, null)], []) };

        var source = AudioRelaySourceProvider.Source(Item(), new PlaceholderMedia(Key, 64_000, null, book), "http://127.0.0.1:8096", []);

        source.Container.ShouldBe("mp3");
    }

    [Fact]
    public async Task OwnItem_NoSources()
    {
        (await Provider().GetMediaSources(Item(), CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task VideoPlaceholder_NoSources()
    {
        var movieId = Guid.NewGuid();
        _lookup.Items[movieId] = new PlaceholderMedia("movie:tmdb:1", 1, null, TestItems.Movie("Sintel", tmdb: "1"));

        (await Provider().GetMediaSources(new Movie { Id = movieId, Path = "/x.strm" }, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task AudioItemWithVideoCatalogEntry_NoSources()
    {
        _lookup.Items[_trackId] = new PlaceholderMedia("movie:tmdb:1", 1, null, TestItems.Movie("Sintel", tmdb: "1"));

        (await Provider().GetMediaSources(Item(), CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public void HideRelaySources_HidesTheRelaySource()
    {
        var source = AudioRelaySourceProvider.Source(Item(), new PlaceholderMedia(Key, 900_000, null, Track()), new FixedRelay().Current, []);
        var response = new PlaybackInfoResponse { MediaSources = [source] };

        PlaybackInfoFilter.HideRelaySources(response, new FixedRelay().Current).ShouldBe(1);

        source.Path.ShouldBe(string.Empty);
        source.SupportsDirectPlay.ShouldBeFalse();
        var json = JsonSerializer.Serialize(response);
        json.ShouldNotContain("JellyShare/v1/play");
        json.ShouldNotContain("127.0.0.1");
    }

    private sealed class Lookup : IPlaceholderLookup
    {
        public Dictionary<Guid, PlaceholderMedia> Items { get; } = new();

        public PlaceholderMedia? Find(Guid itemId) => Items.GetValueOrDefault(itemId);

        public Task<CallerLimits> GetCallerLimitsAsync(CallerContext caller) => Task.FromResult(new CallerLimits(null, null));
    }
}
