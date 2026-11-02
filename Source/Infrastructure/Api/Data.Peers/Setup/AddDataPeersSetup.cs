using JellyShare.Data.Peers.Clients;
using JellyShare.Domain.Interfaces.Peers;
using Microsoft.Extensions.DependencyInjection;

namespace JellyShare.Data.Peers.Setup;

/// <summary>Registers the outbound peer HTTP client.</summary>
public static class DataPeersSetup
{
    /// <summary>
    /// Registers the named peer <see cref="HttpClient"/> (redirects never followed), <see cref="IPeerClient"/> (routing
    /// token and account peers) and <see cref="IAccountSignIn"/> as singletons.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddDataPeersSetup(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient(PeerClient.HttpClientName, c => c.Timeout = PeerClient.HttpClientTimeout)
            .ConfigurePrimaryHttpMessageHandler(PeerClient.CreatePrimaryHandler);
        services.AddSingleton(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return new AccountPeerClient(() => factory.CreateClient(PeerClient.HttpClientName));
        });
        services.AddSingleton<IAccountSignIn>(sp => sp.GetRequiredService<AccountPeerClient>());
        services.AddSingleton<IPeerClient>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return new PeerClientRouter(
                new PeerClient(() => factory.CreateClient(PeerClient.HttpClientName)),
                sp.GetRequiredService<AccountPeerClient>());
        });
        return services;
    }
}
