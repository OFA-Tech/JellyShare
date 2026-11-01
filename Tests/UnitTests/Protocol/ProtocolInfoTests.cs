using JellyShare.Domain.Models.Protocol;

namespace JellyShare.UnitTests.Protocol;

public sealed class ProtocolInfoTests
{
    [Fact]
    public void Version_IsOnePointZero()
    {
        ProtocolInfo.Version.ShouldBe("1.0");
    }
}
