using JellyShare.Application.Features.Placeholders;
using JellyShare.Application.Features.Relay;
using JellyShare.Data.Files.Placeholders;
using JellyShare.Data.Files.Stores;
using JellyShare.Domain.Exceptions;
using JellyShare.Domain.Models.Placeholders;
using JellyShare.Domain.Keys;
using JellyShare.Domain.Models.Protocol;
using JellyShare.UnitTests.Fakes;

namespace JellyShare.UnitTests.Relay;

public sealed class RelayUrlTests
{
    private const string Base = "http://127.0.0.1:8096/jellyfin";

    [Theory]
    [InlineData("movie:tmdb:10378")]
    [InlineData("episode:tvdb:153021:s2e1")]
    [InlineData("odd key/with?chars")]
    public void TryGetKey_RoundTripsStrmContent(string key)
    {
        var url = PlaceholderPaths.StrmContent(Base, key);

        RelayUrl.TryGetKey(url, Base, out var parsed).ShouldBeTrue();
        parsed.ShouldBe(key);
        RelayUrl.IsRelayUrl(url, Base).ShouldBeTrue();
    }

    [Fact]
    public void TryGetKey_OlderRelayBase_StillRecognised()
    {
        var url = PlaceholderPaths.StrmContent("http://127.0.0.1:8096", "movie:tmdb:1");

        RelayUrl.TryGetKey(url, Base, out var key).ShouldBeTrue();
        key.ShouldBe("movie:tmdb:1");
        RelayUrl.IsRelayUrl(url, Base).ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/media/Movies/x.mkv")]
    [InlineData("http://example.org/video.mkv")]
    [InlineData("file:///JellyShare/v1/play/x")]
    public void NonRelayPaths_AreNotRecognised(string? path)
    {
        RelayUrl.IsRelayUrl(path, Base).ShouldBeFalse();
        RelayUrl.TryGetKey(path, Base, out _).ShouldBeFalse();
    }

    [Fact]
    public void ConsumerIndexFiles_FindsByCanonicalAndOtherKeys_AndReloadsOnChange()
    {
        using var dir = new TempDir();
        var path = dir.Sub(MergedIndexStore.FileName);
        var item = TestItems.Movie("M", tmdb: "1", imdb: "tt1");
        MergedIndexStore.Save(path, new[] { new MergedEntry("movie:tmdb:1", new[] { "movie:imdb:tt1", "movie:tmdb:1" }, new List<(Guid, CatalogItem)> { (TestItems.PeerA, item) }) }, DateTimeOffset.UnixEpoch);
        var index = new ConsumerIndexFiles(() => dir.Path);

        index.Find("movie:tmdb:1")!.CanonicalKey.ShouldBe("movie:tmdb:1");
        index.Find("movie:imdb:tt1")!.CanonicalKey.ShouldBe("movie:tmdb:1");
        index.Find("movie:tmdb:2").ShouldBeNull();

        MergedIndexStore.Save(path, Array.Empty<MergedEntry>(), DateTimeOffset.UnixEpoch.AddDays(1));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        index.Find("movie:tmdb:1").ShouldBeNull();
    }

    [Fact]
    public void ConsumerIndexFiles_TransientReadFailure_IsRetriedWithoutAFileChange()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Exclusive file locks are only enforced on Windows.
        }

        using var dir = new TempDir();
        var path = dir.Sub(MergedIndexStore.FileName);
        var item = TestItems.Movie("M", tmdb: "1");
        MergedIndexStore.Save(path, new[] { new MergedEntry("movie:tmdb:1", new[] { "movie:tmdb:1" }, new List<(Guid, CatalogItem)> { (TestItems.PeerA, item) }) }, DateTimeOffset.UnixEpoch);
        var index = new ConsumerIndexFiles(() => dir.Path);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            index.Find("movie:tmdb:1").ShouldBeNull();
        }

        index.Find("movie:tmdb:1")!.CanonicalKey.ShouldBe("movie:tmdb:1");
    }

    [Fact]
    public void ConsumerIndexFiles_TransientReadFailure_KeepsLastGoodIndex()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDir();
        var path = dir.Sub(MergedIndexStore.FileName);
        var item = TestItems.Movie("M", tmdb: "1");
        MergedIndexStore.Save(path, new[] { new MergedEntry("movie:tmdb:1", new[] { "movie:tmdb:1" }, new List<(Guid, CatalogItem)> { (TestItems.PeerA, item) }) }, DateTimeOffset.UnixEpoch);
        var index = new ConsumerIndexFiles(() => dir.Path);
        index.Find("movie:tmdb:1").ShouldNotBeNull();

        MergedIndexStore.Save(path, new[] { new MergedEntry("movie:tmdb:1", new[] { "movie:tmdb:1" }, new List<(Guid, CatalogItem)> { (TestItems.PeerA, item) }) }, DateTimeOffset.UnixEpoch.AddDays(1));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            index.Find("movie:tmdb:1").ShouldNotBeNull();
        }
    }

    [Fact]
    public void ConsumerIndexFiles_UnreadableState_IsEmpty()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Sub(ConsumerIndexFiles.PeerStateFileName), "{not json");
        var index = new ConsumerIndexFiles(() => dir.Path);

        index.PeerStates().ShouldBeEmpty();
    }
}
