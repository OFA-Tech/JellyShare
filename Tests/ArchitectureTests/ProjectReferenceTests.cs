using System.Xml.Linq;

namespace JellyShare.ArchitectureTests;

/// <summary>The project files declare the same layering, and host packages stay in the Jellyfin projects.</summary>
public sealed class ProjectReferenceTests
{
    private static readonly string[] _hostAllowed = ["JellyShare.Jellyfin.csproj", "JellyShare.JellyfinTests.csproj"];

    [Fact]
    public void Domain_DeclaresNoProjectReferences()
        => Solution.ProjectReferences(Solution.Full("Source/Domain/JellyShare.Domain.csproj")).ShouldBeEmpty();

    [Fact]
    public void Application_DeclaresOnlyADomainReference()
        => Solution.ProjectReferences(Solution.Full("Source/Application/JellyShare.Application.csproj"))
            .ShouldBe([Solution.DomainName]);

    [Fact]
    public void Jellyfin_ReferencesDomainApplicationAndEveryModule()
        => Solution.ProjectReferences(Solution.Full("Source/Infrastructure/Platform/Jellyfin/JellyShare.Jellyfin.csproj"))
            .ShouldBe([Solution.DomainName, Solution.ApplicationName, .. Solution.InfrastructureModules.Select(m => m.Assembly)], ignoreOrder: true);

    [Fact]
    public void UnitTests_DoNotReferenceTheJellyfinProject()
        => Solution.ProjectReferences(Solution.Full("Tests/UnitTests/JellyShare.UnitTests.csproj"))
            .ShouldNotContain(Solution.JellyfinName);

    [Fact]
    public void OnlyTheJellyfinProjects_ReferenceJellyfinPackagesOrAspNetCore()
    {
        var violations = Solution.ProjectFiles()
            .Where(p => !_hostAllowed.Contains(Path.GetFileName(p), StringComparer.Ordinal))
            .SelectMany(p => Solution.Load(p)
                .Descendants()
                .Where(e => e.Name.LocalName is "PackageReference" or "FrameworkReference")
                .Select(e => e.Attribute("Include")?.Value ?? string.Empty)
                .Where(IsHost)
                .Select(name => $"{Path.GetFileName(p)}: {name}"))
            .ToArray();

        violations.ShouldBeEmpty("Only JellyShare.Jellyfin and its tests may reference Jellyfin packages or ASP.NET Core.");
    }

    [Theory]
    [InlineData("Source/Domain/JellyShare.Domain.csproj")]
    [InlineData("Source/Application/JellyShare.Application.csproj")]
    public void Layer_PackageReferences_AreCompileOnly(string project)
    {
        // Jellyfin loads every DLL in the plugin folder: host-provided packages must neither ship nor flow on.
        var loose = Solution.Load(Solution.Full(project))
            .Descendants("PackageReference")
            .Where(e => !Has(e, "ExcludeAssets", "runtime") || !Has(e, "PrivateAssets", "all"))
            .Select(e => e.Attribute("Include")?.Value)
            .ToArray();

        loose.ShouldBeEmpty($"{project}: package references need ExcludeAssets=\"runtime\" and PrivateAssets=\"all\".");
    }

    [Fact]
    public void SolutionFile_ListsEveryProjectAtItsRealPath()
    {
        var declared = XDocument.Load(Solution.Full("JellyShare.slnx"))
            .Descendants("Project")
            .Select(e => e.Attribute("Path")?.Value)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .ToArray();

        foreach (var path in declared)
        {
            File.Exists(Solution.Full(path)).ShouldBeTrue($"The solution lists a project that does not exist: {path}.");
        }

        var actual = Solution.ProjectFiles().Select(p => Path.GetRelativePath(Solution.Root(), p).Replace('\\', '/'));
        actual.ShouldBe(declared, ignoreOrder: true);
    }

    private static bool IsHost(string name)
        => name.StartsWith("Jellyfin.", StringComparison.Ordinal)
            || name.StartsWith("MediaBrowser", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal);

    private static bool Has(XElement element, string name, string value)
    {
        var text = element.Attribute(name)?.Value ?? element.Element(name)?.Value ?? string.Empty;
        return text.Split(';', StringSplitOptions.TrimEntries).Contains(value, StringComparer.OrdinalIgnoreCase);
    }
}
