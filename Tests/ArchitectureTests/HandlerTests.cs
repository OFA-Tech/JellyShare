using Mono.Cecil;

namespace JellyShare.ArchitectureTests;

/// <summary>
/// Use cases are handlers in <c>JellyShare.Application.Features.&lt;Area&gt;</c>; their requests are records in
/// <c>JellyShare.Domain.Models.Requests.&lt;Area&gt;</c>. No other project declares a handler.
/// </summary>
public sealed class HandlerTests
{
    private const string HandlerInterface = "JellyShare.Domain.Interfaces.Messaging.IRequestHandler`2";
    private const string RequestInterface = "JellyShare.Domain.Interfaces.Messaging.IRequest`1";
    private const string ValidatorInterface = "JellyShare.Domain.Interfaces.Messaging.IValidator`1";

    [Fact]
    public void Handlers_LiveInApplicationFeatures()
    {
        var handlers = Implementing(Solution.Application.Location, HandlerInterface);

        handlers.ShouldNotBeEmpty();
        handlers.Where(t => !t.Namespace.StartsWith(Solution.ApplicationName + ".Features.", StringComparison.Ordinal))
            .Select(t => t.FullName)
            .ShouldBeEmpty("Handlers belong in JellyShare.Application.Features.<Area>.");
        handlers.Where(t => !t.IsPublic || !t.IsSealed || !t.Name.EndsWith("Handler", StringComparison.Ordinal))
            .Select(t => t.FullName)
            .ShouldBeEmpty("Handlers are public sealed *Handler classes (AddDispatcherSetup registers them by reflection).");
    }

    [Fact]
    public void Validators_LiveInApplicationFeatures()
    {
        Implementing(Solution.Application.Location, ValidatorInterface)
            .Where(t => !t.Namespace.StartsWith(Solution.ApplicationName + ".Features.", StringComparison.Ordinal) || !t.Name.EndsWith("Validator", StringComparison.Ordinal))
            .Select(t => t.FullName)
            .ShouldBeEmpty("Validators are *Validator classes in JellyShare.Application.Features.<Area>.");
    }

    [Fact]
    public void NoOtherProject_DeclaresHandlersOrRequests()
    {
        var paths = Solution.InfrastructureModules.Select(m => Solution.AssemblyPath(m.Assembly)).Append(Solution.JellyfinPath);

        var offending = paths
            .SelectMany(p => Implementing(p, HandlerInterface).Concat(Implementing(p, RequestInterface)))
            .Select(t => t.FullName)
            .ToList();

        offending.ShouldBeEmpty();
    }

    [Fact]
    public void Requests_AreRecordsInDomainModelsRequests()
    {
        var requests = Implementing(Solution.Domain.Location, RequestInterface);

        requests.ShouldNotBeEmpty();
        requests.Where(t => !t.Namespace.StartsWith(Solution.DomainName + ".Models.Requests.", StringComparison.Ordinal) || !t.IsRecord)
            .Select(t => t.FullName)
            .ShouldBeEmpty("Requests are records in JellyShare.Domain.Models.Requests.<Area>.");
        requests.Where(t => !(t.Name.EndsWith("Command", StringComparison.Ordinal) || t.Name.EndsWith("Query", StringComparison.Ordinal)))
            .Select(t => t.FullName)
            .ShouldBeEmpty("Requests are named *Command or *Query.");
        Implementing(Solution.Application.Location, RequestInterface).ShouldBeEmpty();
    }

    internal static bool IsRecord(TypeDefinition type) => type.Methods.Any(m => m.Name == "<Clone>$");

    private static List<TypeSnapshot> Implementing(string path, string openInterface)
    {
        using var module = ModuleDefinition.ReadModule(path);
        return module.Types
            .Where(t => t.IsClass && !t.IsAbstract && Implements(t, openInterface))
            .Select(t => new TypeSnapshot(t.FullName, t.Namespace, t.Name, t.IsPublic, t.IsSealed, IsRecord(t)))
            .ToList();
    }

    private static bool Implements(TypeDefinition type, string openInterface)
    {
        foreach (var implemented in type.Interfaces.Select(i => i.InterfaceType))
        {
            var name = implemented is GenericInstanceType generic ? generic.ElementType.FullName : implemented.FullName;
            if (name == openInterface)
            {
                return true;
            }

            var resolved = Resolve(implemented);
            if (resolved is not null && Implements(resolved, openInterface))
            {
                return true;
            }
        }

        return false;
    }

    private static TypeDefinition? Resolve(TypeReference reference)
    {
        try
        {
            return reference.Resolve();
        }
        catch (AssemblyResolutionException)
        {
            return null;
        }
    }

    private sealed record TypeSnapshot(string FullName, string Namespace, string Name, bool IsPublic, bool IsSealed, bool IsRecord);
}
