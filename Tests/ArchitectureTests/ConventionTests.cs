using System.Text.RegularExpressions;

namespace JellyShare.ArchitectureTests;

/// <summary>Namespaces follow folders, setup files follow the template naming, and the layers do not import the host.</summary>
public sealed partial class ConventionTests
{
    public static TheoryData<string, string> Projects
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var (assembly, folder) in Solution.SourceProjects)
            {
                data.Add(assembly, folder);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void Namespaces_MatchFolders(string assembly, string folder)
    {
        var wrong = new List<string>();
        foreach (var file in Solution.SourceFiles(folder))
        {
            var relative = Path.GetRelativePath(Solution.Full(folder), Path.GetDirectoryName(file)!);
            var expected = relative == "." ? assembly : assembly + "." + relative.Replace(Path.DirectorySeparatorChar, '.');
            var declared = NamespaceLine().Match(File.ReadAllText(file));
            if (!declared.Success || declared.Groups[1].Value != expected)
            {
                wrong.Add($"{Path.GetRelativePath(Solution.Root(), file)}: '{(declared.Success ? declared.Groups[1].Value : "(none)")}' (expected '{expected}')");
            }
        }

        wrong.ShouldBeEmpty("File-scoped namespaces must be the assembly name plus the folder path.");
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void SetupFiles_FollowTheTemplateNaming(string assembly, string folder)
    {
        var setup = Path.Combine(Solution.Full(folder), "Setup");
        Directory.Exists(setup).ShouldBeTrue($"{assembly} needs a Setup folder.");

        var files = Directory.EnumerateFiles(setup, "*.cs").ToList();
        files.ShouldNotBeEmpty();
        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var match = SetupFileName().Match(name);
            match.Success.ShouldBeTrue($"{name}.cs: setup files are named Add{{X}}Setup.cs.");

            var x = match.Groups[1].Value;
            var text = File.ReadAllText(file);
            text.ShouldContain($"public static class {x}Setup", Case.Sensitive, customMessage: $"{name}.cs must declare 'public static class {x}Setup'.");
            text.ShouldContain($"public static IServiceCollection Add{x}Setup(this IServiceCollection services", Case.Sensitive, customMessage: $"{name}.cs must declare 'Add{x}Setup'.");
        }
    }

    [Fact]
    public void Composition_CallsEveryLayerSetup()
    {
        var registrator = File.ReadAllText(Solution.Full("Source/Infrastructure/Platform/Jellyfin/PluginServiceRegistrator.cs"));

        registrator.ShouldContain(".AddDomainSetup(", Case.Sensitive);
        registrator.ShouldContain(".AddApplicationSetup(", Case.Sensitive);
        registrator.ShouldContain(".AddModulesSetup(", Case.Sensitive);
        registrator.ShouldContain(".AddJellyfinSetup(", Case.Sensitive);
    }

    [Theory]
    [InlineData("Source/Domain")]
    [InlineData("Source/Application")]
    public void Layer_SourcesDoNotImportTheHost(string folder)
    {
        var offending = Solution.SourceFiles(folder)
            .SelectMany(f => File.ReadLines(f)
                .Where(l => HostUsing().IsMatch(l))
                .Select(l => $"{Path.GetRelativePath(Solution.Root(), f)}: {l.Trim()}"))
            .ToList();

        offending.ShouldBeEmpty();
    }

    [GeneratedRegex(@"^namespace\s+([\w\.]+);", RegexOptions.Multiline)]
    private static partial Regex NamespaceLine();

    [GeneratedRegex(@"^Add(\w+)Setup$")]
    private static partial Regex SetupFileName();

    [GeneratedRegex(@"^\s*(global\s+)?using\s+(static\s+)?(MediaBrowser|Jellyfin|Emby|Microsoft\.AspNetCore)\b")]
    private static partial Regex HostUsing();
}
