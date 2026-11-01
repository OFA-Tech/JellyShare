using JellyShare.Application.Features.Catalog;
using JellyShare.Application.Setup;
using JellyShare.Domain.Interfaces.Configuration;
using JellyShare.Domain.Interfaces.Messaging;
using JellyShare.Domain.Interfaces.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JellyShare.JellyfinTests.Fakes;

/// <summary>The real dispatcher over the given federation services (what the controller resolves in Jellyfin).</summary>
internal static class FederationSender
{
    public static ISender Create(IServerIdentity identity, CatalogBuilder catalog, ISettingsProvider config, ShareTokenService tokens)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(identity);
        services.AddSingleton(catalog);
        services.AddSingleton(config);
        services.AddSingleton(tokens);
        services.AddDispatcherSetup();
        return services.BuildServiceProvider().GetRequiredService<ISender>();
    }
}