using System.Reflection;
using NetArchTest.Rules;

namespace JellyShare.ArchitectureTests;

/// <summary>
/// Domain depends on nothing, Application only on Domain, and only JellyShare.Jellyfin touches Jellyfin or ASP.NET Core,
/// so a Jellyfin update only changes that project.
/// </summary>
public sealed class LayerDependencyTests
{
    [Fact]
    public void Domain_ReferencesNoOtherJellyShareAssembly()
    {
        Referenced(Solution.Domain)
            .Where(n => n.StartsWith("JellyShare.", StringComparison.Ordinal))
            .ShouldBeEmpty("Domain is the shared kernel: every other project references it, never the reverse.");
    }

    [Fact]
    public void Application_ReferencesOnlyDomain()
    {
        Referenced(Solution.Application)
            .Where(n => n.StartsWith("JellyShare.", StringComparison.Ordinal))
            .ShouldBe([Solution.DomainName]);
    }

    [Theory]
    [InlineData(Solution.DomainName)]
    [InlineData(Solution.ApplicationName)]
    public void Layer_ReferencesNoHostAssembly(string assemblyName)
    {
        var assembly = assemblyName == Solution.DomainName ? Solution.Domain : Solution.Application;

        Referenced(assembly)
            .Where(IsHostAssembly)
            .ShouldBeEmpty($"{assemblyName} must not reference Jellyfin, MediaBrowser or ASP.NET Core assemblies.");
    }

    [Fact]
    public void Domain_HasNoDependencyOnApplicationJellyfinOrAspNetCore()
    {
        var result = Types.InAssembly(Solution.Domain)
            .ShouldNot()
            .HaveDependencyOnAny([Solution.ApplicationName, Solution.JellyfinName, .. Solution.HostNamespaces])
            .GetResult();

        result.IsSuccessful.ShouldBeTrue("Offending Domain types: " + string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Application_HasNoDependencyOnJellyfinOrAspNetCore()
    {
        var result = Types.InAssembly(Solution.Application)
            .ShouldNot()
            .HaveDependencyOnAny([Solution.JellyfinName, .. Solution.HostNamespaces])
            .GetResult();

        result.IsSuccessful.ShouldBeTrue("Offending Application types: " + string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Jellyfin_DependsOnTheHost_SoTheRulesAboveCanFail()
    {
        using var module = Mono.Cecil.ModuleDefinition.ReadModule(Solution.JellyfinPath);

        module.AssemblyReferences.Select(r => r.Name).Where(IsHostAssembly).ShouldNotBeEmpty();
    }

    private static string[] Referenced(Assembly assembly)
        => assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToArray();

    private static bool IsHostAssembly(string name)
        => Solution.HostNamespaces.Any(h => name == h || name.StartsWith(h + ".", StringComparison.Ordinal));
}
