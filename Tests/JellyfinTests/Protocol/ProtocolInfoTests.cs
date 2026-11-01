using JellyShare.Jellyfin.Protocol;
using Xunit;

namespace JellyShare.JellyfinTests.Protocol;

public class ProtocolInfoTests
{
    [Fact]
    public void Version_IsOnePointZero()
    {
        Assert.Equal("1.0", ProtocolInfo.Version);
    }
}
