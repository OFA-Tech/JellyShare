using JellyShare.Application.Features.Catalog;
using JellyShare.Application.Features.Streaming;
using JellyShare.Application.Setup;
using JellyShare.Data.Files.Placeholders;
using JellyShare.Data.Files.Stores;
using JellyShare.Domain.Interfaces.Configuration;
using JellyShare.Domain.Interfaces.Library;
using JellyShare.Domain.Interfaces.Messaging;
using JellyShare.Domain.Interfaces.Peers;
using JellyShare.Domain.Interfaces.Placeholders;
using JellyShare.Domain.Interfaces.Platform;
using JellyShare.Domain.Interfaces.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JellyShare.UnitTests.Fakes;

/// <summary>The real dispatcher (handlers, validators, behaviors) over fakes, as the admin controller sees it.</summary>
internal sealed class AdminHarness : IDisposable
{
    private readonly ServiceProvider _services;

    public AdminHarness()
    {
        Saver = new FakeSaver(Config);
        Config.Settings.MoviesPlaceholderPath = Tmp.Sub("ph", "Movies");
        Config.Settings.ShowsPlaceholderPath = Tmp.Sub("ph", "Shows");
        Index = new ConsumerIndexFiles(() => Tmp.Sub("data"));

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<ISettingsProvider>(Config);
        services.AddSingleton<ISettingsStore>(Saver);
        services.AddSingleton(Tokens);
        services.AddSingleton(Limiter);
        services.AddSingleton<IPeerClient>(Peers);
        services.AddSingleton<ISyncRunner>(Sync);
        services.AddSingleton<IConsumerIndex>(Index);
        services.AddSingleton<IAdminLibraries>(Libraries);
        services.AddSingleton<IPlaceholderFiles>(new PlaceholderWriter());
        services.AddSingleton<IServerIdentity>(new FakeIdentity());
        services.AddDispatcherSetup();
        _services = services.BuildServiceProvider();
    }

    public TempDir Tmp { get; } = new();

    public ConsumerConfig Config { get; } = new();

    public FakeSaver Saver { get; }

    public ShareTokenService Tokens { get; } = new();

    public StreamLimiter Limiter { get; } = new(TimeProvider.System);

    public FakePeerClient Peers { get; } = new();

    public FakeSync Sync { get; } = new();

    public FakeLibraries Libraries { get; } = new();

    public ConsumerIndexFiles Index { get; }

    public ISender Sender => _services.GetRequiredService<ISender>();

    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request) => Sender.Send(request);

    public void Dispose()
    {
        _services.Dispose();
        Tmp.Dispose();
    }
}
