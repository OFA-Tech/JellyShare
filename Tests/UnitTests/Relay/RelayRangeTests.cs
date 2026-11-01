using JellyShare.Application.Features.Relay;

namespace JellyShare.UnitTests.Relay;

public sealed class RelayRangeTests
{
    [Theory]
    [InlineData("bytes=0-1023", "bytes=0-1023")]
    [InlineData("bytes=100-  ", "bytes=100-")]
    [InlineData("BYTES=0-1", "BYTES=0-1")]
    [InlineData("bytes=0-1,5-9", "bytes=0-1,5-9")]
    public void Forwardable_BytesRange_IsKeptTrimmed(string range, string expected)
        => RelayRange.Forwardable(range).ShouldBe(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" bytes=0-1")]
    [InlineData("items=0-1")]
    [InlineData("bytes=0-1\r\nX-Evil: 1")]
    [InlineData("bytes=0-1\u0000")]
    public void Forwardable_EmptyForeignOrControlCharacters_IsDropped(string? range)
        => RelayRange.Forwardable(range).ShouldBeNull();

    [Fact]
    public void Forwardable_LongerThanTheLimit_IsDropped()
    {
        var atLimit = "bytes=" + new string('1', RelayRange.MaxLength - "bytes=".Length);

        RelayRange.Forwardable(atLimit).ShouldBe(atLimit);
        RelayRange.Forwardable(atLimit + "1").ShouldBeNull();
    }
}
