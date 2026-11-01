using JellyShare.Domain.Interfaces.Streaming;
using JellyShare.Media.Runners;
using Microsoft.Extensions.DependencyInjection;

namespace JellyShare.Media.Setup;

/// <summary>Registers the ffmpeg process runner.</summary>
public static class MediaSetup
{
    /// <summary>Registers <see cref="IFfmpegRunner"/> as a singleton.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddMediaSetup(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IFfmpegRunner, FfmpegProcessRunner>();
        return services;
    }
}
