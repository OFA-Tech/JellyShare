using System.Text.RegularExpressions;
using Mono.Cecil;

namespace JellyShare.ArchitectureTests;

/// <summary>
/// Infrastructure projects are template modules: each references only the Domain, exposes exactly one
/// <c>IInfrastructureModule</c>, and is registered through the explicit list in the Jellyfin module loader.
/// </summary>
public sealed partial class ModuleTests
{
    private const string ModuleInterface = "JellyShare.Domain.Interfaces.Modules.IInfrastructureModule";
    private const string LoaderSource = "Source/Infrastructure/Platform/Jellyfin/Modules/InfrastructureModuleLoader.cs";

    public static TheoryData<string, string> Modules
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var (assembly, folder) in Solution.InfrastructureModules)
            {
                data.Add(assembly, folder);
            }

            return data;
        }
    }

    [Fact]
    public void EveryInfrastructureProjectOnDisk_IsAKnownModule()
    {
        var onDisk = Solution.ProjectFiles()
            .Select(p => Path.GetRelativePath(Solution.Root(), p).Replace('\\', '/'))
            .Where(p => p.StartsWith("Source/Infrastructure/", StringComparison.Ordinal) && !p.EndsWith("/JellyShare.Jellyfin.csproj", StringComparison.Ordinal))
            .Select(p => Path.GetFileNameWithoutExtension(p))
            .ToArray();

        onDisk.ShouldBe(Solution.InfrastructureModules.Select(m => m.Assembly), ignoreOrder: true);
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void Module_ProjectReferencesOnlyDomain(string assembly, string folder)
        => Solution.ProjectReferences(Solution.Full($"{folder}/{assembly}.csproj")).ShouldBe([Solution.DomainName]);

    [Theory]
    [MemberData(nameof(Modules))]
    public void Module_AssemblyReferencesOnlyDomain_AndNoHost(string assembly, string folder)
    {
        _ = folder;
        using var module = ModuleDefinition.ReadModule(Solution.AssemblyPath(assembly));
        var references = module.AssemblyReferences.Select(r => r.Name).ToArray();

        references.Where(n => n.StartsWith("JellyShare.", StringComparison.Ordinal)).ShouldBe([Solution.DomainName]);
        references.Where(Solution.IsHostAssembly).ShouldBeEmpty($"{assembly} must not reference Jellyfin, MediaBrowser or ASP.NET Core.");
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void Module_PackageReferences_AreCompileOnly(string assembly, string folder)
        => Solution.LoosePackageReferences(Solution.Full($"{folder}/{assembly}.csproj"))
            .ShouldBeEmpty($"{assembly}: package references need ExcludeAssets=\"runtime\" and PrivateAssets=\"all\" (Jellyfin provides them).");

    [Theory]
    [MemberData(nameof(Modules))]
    public void Module_HasExactlyOneInfrastructureModule_InItsModulesNamespace(string assembly, string folder)
    {
        _ = folder;
        var modules = ModuleTypes(assembly);

        var single = modules.ShouldHaveSingleItem($"{assembly} must declare exactly one IInfrastructureModule.");
        single.Namespace.ShouldBe(assembly + ".Modules");
        single.IsSealed.ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void Module_IsListedInTheLoader(string assembly, string folder)
    {
        _ = folder;
        var name = ModuleTypes(assembly).Single().Name;
        var loader = File.ReadAllText(Solution.Full(LoaderSource));

        loader.ShouldContain($"new {name}()", Case.Sensitive, customMessage: $"{name} must be in the explicit list of InfrastructureModuleLoader.");
    }

    [Fact]
    public void Loader_ListsOnlyKnownModules()
    {
        var known = Solution.InfrastructureModules.Select(m => ModuleTypes(m.Assembly).Single().Name).ToHashSet(StringComparer.Ordinal);
        var listed = ModuleConstruction().Matches(File.ReadAllText(Solution.Full(LoaderSource))).Select(m => m.Groups[1].Value).ToArray();

        listed.ShouldNotBeEmpty();
        listed.Where(n => !known.Contains(n)).ShouldBeEmpty();
        listed.Length.ShouldBe(listed.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Application_ReferencesNoInfrastructureModule()
    {
        var referenced = Solution.Application.GetReferencedAssemblies().Select(a => a.Name).ToArray();

        referenced.ShouldNotContain(n => Solution.InfrastructureModules.Any(m => m.Assembly == n) || n == Solution.JellyfinName);
    }

    private static List<(string Name, string Namespace, bool IsSealed)> ModuleTypes(string assembly)
    {
        using var module = ModuleDefinition.ReadModule(Solution.AssemblyPath(assembly));
        return module.Types
            .Where(t => t.IsClass && !t.IsAbstract && t.Interfaces.Any(i => i.InterfaceType.FullName == ModuleInterface))
            .Select(t => (t.Name, t.Namespace, t.IsSealed))
            .ToList();
    }

    [GeneratedRegex(@"new\s+(\w+Module)\(\)")]
    private static partial Regex ModuleConstruction();
}
