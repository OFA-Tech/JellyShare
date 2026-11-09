using JellyShare.Application.Features.Sync;
using JellyShare.Domain.Keys;
using static JellyShare.UnitTests.Fakes.TestItems;

namespace JellyShare.UnitTests.Sync;

public sealed class LibraryScopesTests
{
    [Fact]
    public void ForEntries_AudioEntries_AreIgnoredAndMoviesKept()
    {
        var index = MergeIndex.Build(new[]
        {
            (PeerA, Track("Hells Bells", mbid: "b9f3a1c2-5d4e-4f6a-8b7c-0123456789ab")),
            (PeerA, Book("Part 1")),
            (PeerA, Movie("Sintel", tmdb: "45745")),
        });

        var scope = LibraryScopes.ForEntries(index.Entries);

        scope.MovieIds["tmdb"].ShouldBe(new[] { "45745" });
        scope.MovieIds.Count.ShouldBe(1);
        scope.SeriesIds.ShouldBeEmpty();
    }

    [Fact]
    public void ForEntries_OnlyAudio_IsEmpty()
        => LibraryScopes.ForEntries(MergeIndex.Build(new[] { (PeerA, Track("Hells Bells")) }).Entries).IsEmpty.ShouldBeTrue();
}
