using Mono.Cecil;

namespace JellyShare.ArchitectureTests;

/// <summary>Ports (interfaces) and enums are declared in the Domain so every layer can use them.</summary>
public sealed class ContractLocationTests
{
    [Theory]
    [MemberData(nameof(NonDomainAssemblyPaths))]
    public void InterfacesAndEnums_AreDeclaredOnlyInDomain(string assemblyName)
    {
        var path = assemblyName == Solution.ApplicationName ? Solution.Application.Location : Solution.AssemblyPath(assemblyName);

        // Nested and non-public types are implementation details of their owner and may stay there.
        var misplaced = TopLevelTypes(path)
            .Where(IsHandWritten)
            .Where(t => !t.IsNested && t.IsPublic && (t.IsInterface || t.IsEnum))
            .Select(t => (t.IsInterface ? "interface " : "enum ") + t.FullName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        misplaced.ShouldBeEmpty($"Move these to JellyShare.Domain (Interfaces/<Area> or Enums): {string.Join(", ", misplaced)}");
    }

    /// <summary>
    /// Records that are implementation details of the use case or adapter that declares them, never part of a contract
    /// between projects (requests, responses, DTOs and port models are in the Domain). Moving them would only scatter
    /// one feature over two projects.
    /// </summary>
    private static readonly string[] _recordExceptions =
    [
        // CatalogBuilder's ETag + lazy body builder (holds a Func); only the catalog handler consumes it.
        "JellyShare.Application.Features.Catalog.CatalogPlan",

        // PathNaming's result (folder + base name of a placeholder); used by the planner and sync only.
        "JellyShare.Application.Features.Placeholders.PlaceholderFile",

        // LocalItemWatcher's sweep outcome and event batch; never leave the watcher (and its tests).
        "JellyShare.Application.Features.Sync.SweepResult",
        "JellyShare.Application.Features.Sync.WatcherBatch",
    ];

    public static TheoryData<string> NonDomainAssemblyPaths
    {
        get
        {
            var data = new TheoryData<string>
            {
                Solution.ApplicationName,
                Solution.JellyfinName,
            };
            foreach (var (assembly, _) in Solution.InfrastructureModules)
            {
                data.Add(assembly);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(NonDomainAssemblyPaths))]
    public void PublicRecords_AreDeclaredInDomain_ExceptTheDocumentedImplementationRecords(string assemblyName)
    {
        var path = assemblyName == Solution.ApplicationName ? Solution.Application.Location : Solution.AssemblyPath(assemblyName);
        using var module = ModuleDefinition.ReadModule(path);

        var misplaced = module.Types
            .Where(IsHandWritten)
            .Where(t => !t.IsNested && t.IsPublic && t.Methods.Any(m => m.Name == "<Clone>$"))
            .Select(t => t.FullName)
            .Where(n => !_recordExceptions.Contains(n, StringComparer.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        misplaced.ShouldBeEmpty($"Move these records to JellyShare.Domain.Models (or document them as implementation records): {string.Join(", ", misplaced)}");
    }

    [Fact]
    public void RecordExceptions_StillExist()
    {
        var paths = new[] { Solution.Application.Location, Solution.JellyfinPath }
            .Concat(Solution.InfrastructureModules.Select(m => Solution.AssemblyPath(m.Assembly)));
        var all = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            using var module = ModuleDefinition.ReadModule(path);
            all.UnionWith(module.Types.Select(t => t.FullName));
        }

        _recordExceptions.Where(e => !all.Contains(e)).ShouldBeEmpty("Remove stale entries from the record exception list.");
    }

    [Fact]
    public void DomainInterfaces_LiveUnderInterfaces()
    {
        var misplaced = TopLevelTypes(Solution.Domain.Location)
            .Where(t => IsHandWritten(t) && !t.IsNested && t.IsInterface)
            .Where(t => !t.Namespace.StartsWith(Solution.DomainName + ".Interfaces.", StringComparison.Ordinal))
            .Select(t => t.FullName)
            .ToList();

        misplaced.ShouldBeEmpty("Domain ports belong in JellyShare.Domain.Interfaces.<Area>.");
    }

    [Fact]
    public void DomainEnums_LiveUnderEnums()
    {
        var misplaced = TopLevelTypes(Solution.Domain.Location)
            .Where(t => IsHandWritten(t) && !t.IsNested && t.IsEnum)
            .Where(t => t.Namespace != Solution.DomainName + ".Enums")
            .Select(t => t.FullName)
            .ToList();

        misplaced.ShouldBeEmpty("Domain enums belong in JellyShare.Domain.Enums.");
    }

    private static List<TypeDefinition> TopLevelTypes(string path)
    {
        using var module = ModuleDefinition.ReadModule(path);
        return module.Types.ToList();
    }

    private static bool IsHandWritten(TypeDefinition type)
        => !string.IsNullOrEmpty(type.Namespace)
            && type.Namespace.StartsWith("JellyShare.", StringComparison.Ordinal)
            && !type.Name.Contains('<', StringComparison.Ordinal)
            && !type.CustomAttributes.Any(a => a.AttributeType.Name is "CompilerGeneratedAttribute" or "GeneratedCodeAttribute" or "EmbeddedAttribute");
}
