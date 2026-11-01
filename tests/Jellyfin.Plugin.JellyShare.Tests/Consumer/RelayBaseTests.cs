using Jellyfin.Plugin.JellyShare.Consumer;
using Xunit;

namespace Jellyfin.Plugin.JellyShare.Tests.Consumer;

public class RelayBaseTests
{
    [Theory]
    [InlineData(null, 8096, "/jellyfin", "http://127.0.0.1:8096/jellyfin")]
    [InlineData(null, 8096, "", "http://127.0.0.1:8096")]
    [InlineData(null, 8096, null, "http://127.0.0.1:8096")]
    [InlineData("", 8096, "jellyfin/", "http://127.0.0.1:8096/jellyfin")]
    [InlineData("  ", 9000, "/a/b/", "http://127.0.0.1:9000/a/b")]
    [InlineData(null, 8096, "/", "http://127.0.0.1:8096")]
    [InlineData("http://10.0.0.2:8096/jf/", 8096, "/jellyfin", "http://10.0.0.2:8096/jf")]
    [InlineData(" http://127.0.0.1:1234 ", 8096, "", "http://127.0.0.1:1234")]
    public void RelayBase_Resolve(string? overrideValue, int port, string? baseUrl, string expected)
        => Assert.Equal(expected, RelayBase.Resolve(overrideValue, port, baseUrl));
}
