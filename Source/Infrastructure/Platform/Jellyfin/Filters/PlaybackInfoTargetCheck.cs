using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JellyShare.Jellyfin.Filters;

/// <summary>Logs a warning once the server has started if <see cref="PlaybackInfoFilter"/>'s target action is missing.</summary>
public sealed class PlaybackInfoTargetCheck : IHostedService
{
    private readonly IActionDescriptorCollectionProvider _actions;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<PlaybackInfoTargetCheck> _logger;
    private CancellationTokenRegistration _registration;

    /// <summary>Initializes a new instance of the <see cref="PlaybackInfoTargetCheck"/> class.</summary>
    /// <param name="actions">The MVC action descriptors.</param>
    /// <param name="lifetime">The host lifetime (the check runs once the server has started).</param>
    /// <param name="logger">The logger.</param>
    public PlaybackInfoTargetCheck(IActionDescriptorCollectionProvider actions, IHostApplicationLifetime lifetime, ILogger<PlaybackInfoTargetCheck> logger)
    {
        _actions = actions;
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _registration = _lifetime.ApplicationStarted.Register(Check);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _registration.Dispose();
        return Task.CompletedTask;
    }

    private void Check()
    {
        try
        {
            if (PlaybackInfoFilter.TargetWarning(_actions.ActionDescriptors.Items) is { } warning)
            {
                _logger.LogWarning("{Warning}", warning);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "JellyShare: could not check the PlaybackInfo action");
        }
    }
}
