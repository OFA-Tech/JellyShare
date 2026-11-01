using System.Reflection;
using System.Xml.Linq;

namespace JellyShare.ArchitectureTests;

/// <summary>Where the projects, assemblies and sources of the solution are.</summary>
internal static class Solution
{
    public const string DomainName = "JellyShare.Domain";
    public const string ApplicationName = "JellyShare.Application";
    public const string JellyfinName = "JellyShare.Jellyfin";

    public static readonly (string Assembly, string Folder)[] InfrastructureModules =
    [
        ("JellyShare.Data.Peers", "Source/Infrastructure/Api/Data.Peers"),
        ("JellyShare.Data.Files", "Source/Infrastructure/Data/Data.Files"),
        ("JellyShare.Media", "Source/Infrastructure/Platform/Media"),
    ];

    public static readonly string[] HostNamespaces = ["MediaBrowser", "Jellyfin", "Emby", "Microsoft.AspNetCore"];

    public static readonly (string Assembly, string Folder)[] SourceProjects =
    [
        (DomainName, "Source/Domain"),
        (ApplicationName, "Source/Application"),
        (JellyfinName, "Source/Infrastructure/Platform/Jellyfin"),
        .. InfrastructureModules,
    ];

    public static Assembly Domain => typeof(global::JellyShare.Domain.Setup.DomainSetup).Assembly;

    public static Assembly Application => typeof(global::JellyShare.Application.Setup.ApplicationSetup).Assembly;

    /// <summary>Gets the plugin assembly path. Its types are read with Cecil only: the Jellyfin host assemblies are not here.</summary>
    public static string JellyfinPath => Path.Combine(AppContext.BaseDirectory, JellyfinName + ".dll");

    public static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JellyShare.slnx")))
        {
            directory = directory.Parent;
        }

        directory.ShouldNotBeNull("Could not locate the repository root from the test output directory.");
        return directory.FullName;
    }

    public static string Full(string relative) => Path.Combine(Root(), relative.Replace('/', Path.DirectorySeparatorChar));

    public static IEnumerable<string> ProjectFiles()
        => Directory.EnumerateFiles(Root(), "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj" or "harness" or ".git"));

    public static IEnumerable<string> SourceFiles(string folder)
        => Directory.EnumerateFiles(Full(folder), "*.cs", SearchOption.AllDirectories)
            .Where(p => !Path.GetRelativePath(Full(folder), p).Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"));

    public static XDocument Load(string projectPath) => XDocument.Load(projectPath);

    /// <summary>Gets the path of a JellyShare assembly in the test output (read with Cecil; never loaded).</summary>
    public static string AssemblyPath(string assemblyName) => Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");

    public static bool IsHostAssembly(string name)
        => HostNamespaces.Any(h => name == h || name.StartsWith(h + ".", StringComparison.Ordinal));

    /// <summary>Lists package references that would ship or flow on (missing ExcludeAssets=runtime or PrivateAssets=all).</summary>
    public static string?[] LoosePackageReferences(string projectPath)
        => Load(projectPath)
            .Descendants("PackageReference")
            .Where(e => !Has(e, "ExcludeAssets", "runtime") || !Has(e, "PrivateAssets", "all"))
            .Select(e => e.Attribute("Include")?.Value)
            .ToArray();

    private static bool Has(XElement element, string name, string value)
    {
        var text = element.Attribute(name)?.Value ?? element.Element(name)?.Value ?? string.Empty;
        return text.Split(';', StringSplitOptions.TrimEntries).Contains(value, StringComparer.OrdinalIgnoreCase);
    }

    public static string[] ProjectReferences(string projectPath)
        => Load(projectPath)
            .Descendants("ProjectReference")
            .Select(e => e.Attribute("Include")?.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => Path.GetFileNameWithoutExtension(v!.Replace('\\', '/')))
            .ToArray();
}
