using JellyShare.Application.Features.Placeholders;
using JellyShare.Domain.Interfaces.Placeholders;
using JellyShare.Domain.Interfaces.Sync;
using JellyShare.Domain.Keys;
using JellyShare.Domain.Models.Placeholders;
using JellyShare.Domain.Models.Sync;
using JellyShare.UnitTests.Fakes;
using JellyShare.UnitTests.Sync;
using Microsoft.Extensions.Logging.Abstractions;

namespace JellyShare.UnitTests.Placeholders;

public sealed class PlaceholderMetadataWriterTests : IDisposable
{
    private static readonly TrackMetadata _scanned = new("01 First Light", [], [], null, null, null, false);

    private readonly FakeMetadata _library = new();
    private readonly FakeIndex _index = new();
    private readonly ConsumerConfig _config = new();
    private readonly RecordingTimeProvider _clock = new();
    private readonly PlaceholderMetadataWriter _writer;

    public PlaceholderMetadataWriterTests()
    {
        _config.Settings.MusicPlaceholderPath = "/ph/Music";
        _config.Settings.AudiobooksPlaceholderPath = "/ph/Audiobooks";
        _writer = new PlaceholderMetadataWriter(_library, _index, _config, _clock, NullLogger<PlaceholderMetadataWriter>.Instance);
    }

    public void Dispose() => _writer.Dispose();

    private MergedEntry Add(Guid itemId, string name, int track)
    {
        var entry = PathNamingTests.Entry(TestItems.Track(name, albumArtist: "Artist B", album: "Album Two", track: track, id: name));
        _index.Entries[entry.CanonicalKey] = entry;
        _library.Tracks[itemId] = new PlaceholderTrack(itemId, entry.CanonicalKey, _scanned);
        return entry;
    }

    [Fact]
    public async Task Run_WritesFreshPlaceholders_ThenNothingTheSecondTime()
    {
        var first = Guid.NewGuid();
        var entry = Add(first, "First Light", 1);
        Add(Guid.NewGuid(), "Second Wind", 2);

        (await _writer.RunAsync(CancellationToken.None)).ShouldBe(2);
        _library.Written.Count.ShouldBe(1);
        TrackMetadataRules.Same(_library.Written[0].Single(t => t.ItemId == first).Metadata, TrackMetadataRules.Wanted(entry)).ShouldBeTrue();
        _library.Roots.ShouldBe(["/ph/Music", "/ph/Audiobooks"]);

        (await _writer.RunAsync(CancellationToken.None)).ShouldBe(0);
        _library.Written.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Run_OwnerHasTheArtist_WritesOwnersSpelling()
    {
        _library.OwnerArtists.Add("ARTIST B");
        var id = Guid.NewGuid();
        Add(id, "First Light", 1);

        await _writer.RunAsync(CancellationToken.None);

        var written = _library.Written.ShouldHaveSingleItem().ShouldHaveSingleItem().Metadata;
        written.AlbumArtists.ShouldBe(["ARTIST B"]);
        written.Artists.ShouldBe(["ARTIST B"]);
    }

    [Fact]
    public async Task Run_PlaceholderWithoutIndexEntry_IsLeftAlone()
    {
        _library.Tracks[Guid.NewGuid()] = new PlaceholderTrack(Guid.NewGuid(), "track:name:unknown", _scanned);

        (await _writer.RunAsync(CancellationToken.None)).ShouldBe(0);
        _library.Written.ShouldBeEmpty();
    }

    [Fact]
    public async Task Run_BlankRootsAreNotRead()
    {
        _config.Settings.AudiobooksPlaceholderPath = " ";

        await _writer.RunAsync(CancellationToken.None);

        _library.Roots.ShouldBe(["/ph/Music"]);
    }

    [Fact]
    public async Task Start_RunsOnceAfterStartup_AndAgainAfterAddedItems_Debounced()
    {
        await _writer.StartAsync(CancellationToken.None);
        var timer = _clock.Timers.ShouldHaveSingleItem();
        timer.DueTime.ShouldBe(PlaceholderMetadataWriter.Delay);

        timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _library.RaiseAdded();
        timer.DueTime.ShouldBe(PlaceholderMetadataWriter.Delay);

        Add(Guid.NewGuid(), "First Light", 1);
        timer.Fire();
        await _library.WriteCalled.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await _writer.StopAsync(CancellationToken.None);
        _library.Subscribers.ShouldBe(0);
        timer.Disposed.ShouldBeTrue();
    }

    private sealed class FakeMetadata : IPlaceholderMetadata
    {
        private Action? _added;

        public Dictionary<Guid, PlaceholderTrack> Tracks { get; } = new();

        public List<IReadOnlyList<PlaceholderTrack>> Written { get; } = new();

        public List<string> Roots { get; } = new();

        public int Subscribers { get; private set; }

        public TaskCompletionSource WriteCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void RaiseAdded() => _added?.Invoke();

        public IDisposable SubscribeAdded(Action handler)
        {
            _added += handler;
            Subscribers++;
            return new Unsubscribe(() =>
            {
                _added -= handler;
                Subscribers--;
            });
        }

        public List<string> OwnerArtists { get; } = new();

        public PlaceholderAudio ReadTracks(IReadOnlyList<string> roots)
        {
            Roots.Clear();
            Roots.AddRange(roots);
            return new PlaceholderAudio(Tracks.Values.ToList(), OwnerArtists.ToList());
        }

        public Task WriteAsync(IReadOnlyList<PlaceholderTrack> tracks, CancellationToken ct)
        {
            Written.Add(tracks);
            foreach (var track in tracks)
            {
                Tracks[track.ItemId] = track;
            }

            WriteCalled.TrySetResult();
            return Task.CompletedTask;
        }

        private sealed class Unsubscribe(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }

    private sealed class FakeIndex : IConsumerIndex
    {
        public Dictionary<string, MergedEntry> Entries { get; } = new(StringComparer.Ordinal);

        public MergedEntry? Find(string canonicalKey) => Entries.GetValueOrDefault(canonicalKey);

        public IReadOnlyDictionary<Guid, PeerState> PeerStates() => new Dictionary<Guid, PeerState>();
    }
}
