using System.Reflection;
using System.Text.Json;
using JellyShare.Application.Features.Catalog;
using JellyShare.Application.Features.Streaming;
using JellyShare.Domain.Interfaces.Platform;
using JellyShare.Jellyfin.Controllers.V1;
using JellyShare.UnitTests.Fakes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace JellyShare.JellyfinTests.Controllers;

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
        return new FederationController(identity, new CatalogBuilder(source, identity, config), source, config, new ShareTokenService(), new StreamLimiter(TimeProvider.System), FakeCapped.Create(), Microsoft.Extensions.Logging.Abstractions.NullLogger<FederationController>.Instance);
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
