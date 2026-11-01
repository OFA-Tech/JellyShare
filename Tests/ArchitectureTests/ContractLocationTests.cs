using Mono.Cecil;

namespace JellyShare.ArchitectureTests;

/// <summary>Ports (interfaces) and enums are declared in the Domain so every layer can use them.</summary>
public sealed class ContractLocationTests
{
    public static TheoryData<string> NonDomainAssemblies => [Solution.ApplicationName, Solution.JellyfinName];

    [Theory]
    [MemberData(nameof(NonDomainAssemblies))]
    public void InterfacesAndEnums_AreDeclaredOnlyInDomain(string assemblyName)
    {
        var path = assemblyName == Solution.ApplicationName ? Solution.Application.Location : Solution.JellyfinPath;

        // Nested and non-public types are implementation details of their owner and may stay there.
        var misplaced = TopLevelTypes(path)
            .Where(IsHandWritten)
            .Where(t => !t.IsNested && t.IsPublic && (t.IsInterface || t.IsEnum))
            .Select(t => (t.IsInterface ? "interface " : "enum ") + t.FullName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        misplaced.ShouldBeEmpty($"Move these to JellyShare.Domain (Interfaces/<Area> or Enums): {string.Join(", ", misplaced)}");
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
