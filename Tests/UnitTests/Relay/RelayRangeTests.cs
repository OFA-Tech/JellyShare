using JellyShare.Application.Features.Relay;
using Xunit;

namespace JellyShare.UnitTests.Relay;

public sealed class RelayRangeTests
{
    [Theory]
    [InlineData("bytes=0-1023", "bytes=0-1023")]
    [InlineData("bytes=100-  ", "bytes=100-")]
    [InlineData("BYTES=0-1", "BYTES=0-1")]
    [InlineData("bytes=0-1,5-9", "bytes=0-1,5-9")]
    public void Forwardable_BytesRange_IsKeptTrimmed(string range, string expected)
        => Assert.Equal(expected, RelayRange.Forwardable(range));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" bytes=0-1")]
    [InlineData("items=0-1")]
    [InlineData("bytes=0-1\r\nX-Evil: 1")]
    [InlineData("bytes=0-1\u0000")]
    public void Forwardable_EmptyForeignOrControlCharacters_IsDropped(string? range)
        => Assert.Null(RelayRange.Forwardable(range));

    [Fact]
    public void Forwardable_LongerThanTheLimit_IsDropped()
    {
        var atLimit = "bytes=" + new string('1', RelayRange.MaxLength - "bytes=".Length);

        Assert.Equal(atLimit, RelayRange.Forwardable(atLimit));
        Assert.Null(RelayRange.Forwardable(atLimit + "1"));
    }
}
