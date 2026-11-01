using JellyShare.Domain.Models.Protocol;
using Xunit;

namespace JellyShare.UnitTests.Protocol;

public class ProtocolInfoTests
{
    [Fact]
    public void Version_IsOnePointZero()
    {
        Assert.Equal("1.0", ProtocolInfo.Version);
    }
}
