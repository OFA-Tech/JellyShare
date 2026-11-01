using System.Reflection;
using System.Text.Json;
using JellyShare.Application.Features.Catalog;
using JellyShare.Application.Features.Streaming;
using JellyShare.Domain.Interfaces.Platform;
using JellyShare.Jellyfin.Controllers.V1;
using JellyShare.JellyfinTests.Fakes;
using JellyShare.UnitTests.Fakes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JellyShare.JellyfinTests.Controllers;

public sealed class FederationControllerPingTests
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
        var catalog = new CatalogBuilder(source, identity, config);
        var tokens = new ShareTokenService();
        return new FederationController(FederationSender.Create(identity, catalog, config, tokens), catalog, source, config, tokens, new StreamLimiter(TimeProvider.System), FakeCapped.Create(), Microsoft.Extensions.Logging.Abstractions.NullLogger<FederationController>.Instance);
    }

    [Fact]
    public async Task Ping_ReturnsProtocolVersionAndServerName()
    {
        var controller = NewController("Alice");

        var result = (await controller.Ping());

        result.Value.ShouldNotBeNull();
        result.Value!.ProtocolVersion.ShouldBe("1.0");
        result.Value.ServerName.ShouldBe("Alice");
    }

    [Fact]
    public async Task Ping_SerializesToCamelCaseWireShape()
    {
        var controller = NewController("Alice");

        var json = JsonSerializer.Serialize((await controller.Ping()).Value);

        json.ShouldBe("{\"protocolVersion\":\"1.0\",\"serverName\":\"Alice\"}");
    }

    [Fact]
    public void Ping_IsAnonymousGetUnderJellyShareV1()
    {
        var type = typeof(FederationController);
        var route = type.GetCustomAttribute<RouteAttribute>();
        route.ShouldNotBeNull();
        route!.Template.ShouldBe("JellyShare/v1");

        var method = type.GetMethod(nameof(FederationController.Ping))!;
        method.GetCustomAttribute<AllowAnonymousAttribute>().ShouldNotBeNull();
        var get = method.GetCustomAttribute<HttpGetAttribute>();
        get.ShouldNotBeNull();
        get!.Template.ShouldBe("ping");
    }
}
