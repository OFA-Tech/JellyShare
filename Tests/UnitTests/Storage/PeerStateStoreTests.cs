using JellyShare.Data.Files.Placeholders;
using JellyShare.Data.Files.Stores;
using JellyShare.Domain.Exceptions;
using JellyShare.Domain.Models.Placeholders;
using JellyShare.Domain.Models.Sync;
using JellyShare.UnitTests.Fakes;
using Xunit;

namespace JellyShare.UnitTests.Storage;

public class PeerStateStoreTests
{
    [Fact]
    public void Load_MissingFile_IsEmpty()
    {
        using var tmp = new TempDir();
        Assert.Empty(new PeerStateStore(tmp.Sub("peer-state.json")).Load());
    }

    [Fact]
    public void Load_RoundTrip()
    {
        using var tmp = new TempDir();
        var store = new PeerStateStore(tmp.Sub("peer-state.json"));
        var id = Guid.NewGuid();
        store.Save(new Dictionary<Guid, PeerState> { [id] = new() { Etag = "e", LastError = "x" } });
        Assert.Equal("e", store.Load()[id].Etag);
    }

    [Fact]
    public void Load_CorruptFile_ThrowsAndKeepsTheFile()
    {
        using var tmp = new TempDir();
        var path = tmp.Sub("peer-state.json");
        File.WriteAllText(path, "{ not json");

        Assert.Throws<PeerStateUnreadableException>(() => new PeerStateStore(path).Load());
        Assert.Equal("{ not json", File.ReadAllText(path));
    }

    [Fact]
    public void Load_LockedFile_Throws()
    {
        using var tmp = new TempDir();
        var path = tmp.Sub("peer-state.json");
        File.WriteAllText(path, "{}");
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        if (OperatingSystem.IsWindows())
        {
            Assert.Throws<PeerStateUnreadableException>(() => new PeerStateStore(path).Load());
        }
    }
}
