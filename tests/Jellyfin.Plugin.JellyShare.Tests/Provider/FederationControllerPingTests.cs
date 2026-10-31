using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.JellyShare.Protocol;
using Jellyfin.Plugin.JellyShare.Provider;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.JellyShare.Tests.Provider;

public class FederationControllerPingTests
{
    private sealed class FakeServerIdentity(string name) : IServerIdentity
    {
        public string ServerName => name;
    }

    private static FederationController NewController(string name)
    {
        var identity = new FakeServerIdentity(name);
        var config = new FakeConfig();
        var source = new FakeSource();
        return new FederationController(identity, new CatalogBuilder(source, identity, config), source, config, new ShareTokenService());
    }

    [Fact]
    public void Ping_ReturnsProtocolVersionAndServerName()
    {
        var controller = NewController("Alice");

        var result = controller.Ping();

        Assert.NotNull(result.Value);
        Assert.Equal("1.0", result.Value!.ProtocolVersion);
        Assert.Equal("Alice", result.Value.ServerName);
    }

    [Fact]
    public void Ping_SerializesToCamelCaseWireShape()
    {
        var controller = NewController("Alice");

        var json = JsonSerializer.Serialize(controller.Ping().Value);

        Assert.Equal("{\"protocolVersion\":\"1.0\",\"serverName\":\"Alice\"}", json);
    }

    [Fact]
    public void Ping_IsAnonymousGetUnderJellyShareV1()
    {
        var type = typeof(FederationController);
        var route = type.GetCustomAttribute<RouteAttribute>();
        Assert.NotNull(route);
        Assert.Equal("JellyShare/v1", route!.Template);

        var method = type.GetMethod(nameof(FederationController.Ping))!;
        Assert.NotNull(method.GetCustomAttribute<AllowAnonymousAttribute>());
        var get = method.GetCustomAttribute<HttpGetAttribute>();
        Assert.NotNull(get);
        Assert.Equal("ping", get!.Template);
    }
}
