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
    [InlineData(" http://127.0.0.1:1234 ", 8096, "", "http://127.0.0.1:1234")]
    [InlineData("http://localhost:8096/jf/", 8096, "", "http://localhost:8096/jf")]
    [InlineData("http://[::1]:8096", 8096, "", "http://[::1]:8096")]
    [InlineData("http://127.0.0.2:8096", 8096, "", "http://127.0.0.2:8096")]
    // A non-loopback override is a configuration error (the relay only answers loopback callers): it is ignored here
    // and the sync refuses to run with OverrideError.
    [InlineData("http://10.0.0.2:8096/jf/", 8096, "/jellyfin", "http://127.0.0.1:8096/jellyfin")]
    public void RelayBase_Resolve(string? overrideValue, int port, string? baseUrl, string expected)
        => Assert.Equal(expected, RelayBase.Resolve(overrideValue, port, baseUrl));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://127.0.0.1:8096")]
    [InlineData("http://localhost:8096/jellyfin")]
    [InlineData("https://[::1]:8920")]
    public void RelayBase_OverrideError_LoopbackOrUnset_IsNull(string? value)
        => Assert.Null(RelayBase.OverrideError(value));

    [Theory]
    [InlineData("http://10.0.0.2:8096")]
    [InlineData("http://jellyfin.example.com")]
    [InlineData("ftp://127.0.0.1")]
    [InlineData("not a url")]
    [InlineData("http://127.0.0.1:8096/?x=1")]
    public void RelayBase_OverrideError_NonLoopback_IsClearError(string value)
        => Assert.Contains("RelayBaseOverride", RelayBase.OverrideError(value), StringComparison.Ordinal);
}
