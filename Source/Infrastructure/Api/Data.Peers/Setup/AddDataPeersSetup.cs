using JellyShare.Data.Peers.Clients;
using JellyShare.Domain.Interfaces.Peers;
using Microsoft.Extensions.DependencyInjection;

namespace JellyShare.Data.Peers.Setup;

/// <summary>Registers the outbound peer HTTP client.</summary>
public static class DataPeersSetup
{
    /// <summary>
    /// Registers the named peer <see cref="HttpClient"/> (redirects never followed) and <see cref="IPeerClient"/> as a
    /// singleton.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddDataPeersSetup(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient(PeerClient.HttpClientName, c => c.Timeout = PeerClient.HttpClientTimeout)
            .ConfigurePrimaryHttpMessageHandler(PeerClient.CreatePrimaryHandler);
        services.AddSingleton<IPeerClient>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return new PeerClient(() => factory.CreateClient(PeerClient.HttpClientName));
        });
        return services;
    }
}
