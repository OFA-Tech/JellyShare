using Jellyfin.Plugin.JellyShare.Protocol;
using Xunit;

namespace Jellyfin.Plugin.JellyShare.Tests.Protocol;

public class ProtocolInfoTests
{
    [Fact]
    public void Version_IsOnePointZero()
    {
        Assert.Equal("1.0", ProtocolInfo.Version);
    }
}
