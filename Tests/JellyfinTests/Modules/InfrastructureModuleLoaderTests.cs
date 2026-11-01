using JellyShare.Data.Files.Modules;
using JellyShare.Data.Peers.Modules;
using JellyShare.Domain.Interfaces.Modules;
using JellyShare.Domain.Interfaces.Peers;
using JellyShare.Domain.Interfaces.Placeholders;
using JellyShare.Domain.Interfaces.Storage;
using JellyShare.Domain.Interfaces.Streaming;
using JellyShare.Domain.Interfaces.Sync;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Jellyfin.Modules;
using JellyShare.Jellyfin.Setup;
using JellyShare.Media.Modules;
using JellyShare.UnitTests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JellyShare.JellyfinTests.Modules;

public sealed class InfrastructureModuleLoaderTests
{
    private static readonly InfrastructureModuleContext Context = new(new JellyShareSettings(), () => throw new InvalidOperationException("not resolved at registration"));

    [Fact]
    public void DefaultLoader_ListsPeersFilesAndMedia_InThatOrder()
        => Assert.Equal(["Peers", "Files", "Media"], new InfrastructureModuleLoader().Modules.Select(m => m.Name));

    [Fact]
    public void DuplicateNames_AreRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new InfrastructureModuleLoader([new PeersModule(), new FakeModule("Peers", true)]));

        Assert.Contains("Peers", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RegistersOnlyEnabledModules_AndTheLoaderItself()
    {
        var on = new FakeModule("On", true);
        var off = new FakeModule("Off", false);
        var loader = new InfrastructureModuleLoader([on, off]);
        var services = new ServiceCollection();

        var loaded = loader.Load(services, Context);

        Assert.Equal(["On"], loaded);
        Assert.Equal(loaded, loader.Loaded);
        Assert.True(on.Registered);
        Assert.False(off.Registered);
        Assert.Contains(services, d => d.ServiceType == typeof(InfrastructureModuleLoader) && d.ImplementationInstance == loader);
    }

    [Fact]
    public void Load_NeverResolvesTheDataFolder()
    {
        var services = new ServiceCollection();

        new InfrastructureModuleLoader().Load(services, Context);

        Assert.NotEmpty(services);
    }

    [Fact]
    public void LogLoaded_NamesTheEnabledModules()
    {
        var loader = new InfrastructureModuleLoader([new FakeModule("A", true), new FakeModule("B", true)]);
        loader.Load(new ServiceCollection(), Context);
        var logger = new LevelLogger<InfrastructureModuleLoader>();

        loader.LogLoaded(logger);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("JellyShare infrastructure modules enabled: A, B.", entry.Message);
    }

    [Theory]
    [InlineData(typeof(PeersModule), new[] { typeof(IPeerClient) })]
    [InlineData(typeof(FilesModule), new[] { typeof(IPeerStateStore), typeof(IMergedIndexStore), typeof(ISyncReportStore), typeof(IPlaceholderLedgerStore), typeof(IPendingScanStore), typeof(IPlaceholderFiles), typeof(IConsumerIndex) })]
    [InlineData(typeof(MediaModule), new[] { typeof(IFfmpegRunner) })]
    public void Module_RegistersItsPortsAsSingletons(Type moduleType, Type[] ports)
    {
        var module = (IInfrastructureModule)Activator.CreateInstance(moduleType)!;
        var services = new ServiceCollection();

        Assert.True(module.IsEnabled(Context));
        module.Register(services, Context);

        foreach (var port in ports)
        {
            var descriptor = Assert.Single(services, d => d.ServiceType == port);
            Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        }
    }

    [Fact]
    public void ModulesSetup_RegistersTheModules_AndTheStartupLog()
    {
        var services = new ServiceCollection();

        services.AddModulesSetup(new JellyShareSettings(), () => "/data");

        Assert.Contains(services, d => d.ServiceType == typeof(IPeerClient));
        Assert.Contains(services, d => d.ServiceType == typeof(IFfmpegRunner));
        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(InfrastructureModuleLog));
    }

    private sealed class FakeModule(string name, bool enabled) : IInfrastructureModule
    {
        public bool Registered { get; private set; }

        public string Name => name;

        public bool IsEnabled(InfrastructureModuleContext context) => enabled;

        public string Describe(InfrastructureModuleContext context) => name;

        public void Register(IServiceCollection services, InfrastructureModuleContext context) => Registered = true;
    }
}
