using System.Reflection;
using Palladin.Module.Agents;
using Palladin.Module.Audit;
using Palladin.Module.Identity;
using Palladin.Module.Notification;
using Palladin.Module.Search;
using Palladin.Module.Vault;
using MassTransit;

namespace Palladin.Tests.Unit.Architecture;

public sealed class MassTransitConsumerArchitectureTests
{
    private static readonly Assembly[] ModuleAssemblies =
    [
        typeof(AgentsModule).Assembly,
        typeof(AuditModule).Assembly,
        typeof(IdentityModule).Assembly,
        typeof(NotificationModule).Assembly,
        typeof(SearchModule).Assembly,
        typeof(VaultModule).Assembly
    ];

    [Fact]
    public void Consumers_ShouldLiveInFeaturesOrTriggers()
    {
        var misplaced = ConsumerTypes()
            .Where(type => !HasNamespaceSegment(type, "Features") && !HasNamespaceSegment(type, "Triggers"))
            .Select(type => type.FullName)
            .ToList();

        misplaced.ShouldBeEmpty("command consumers belong to Features and event consumers belong to Triggers");
    }

    [Fact]
    public void FeaturesAndTriggers_ShouldNotDependOnDbContext()
    {
        var violations = ApplicationFiles()
            .Where(file => ContainsAny(File.ReadAllText(file), "DbContext", "DbReadContext", "DbWriteContext"))
            .Select(Path.GetFileName)
            .ToList();

        violations.ShouldBeEmpty("Features and Triggers must use only split Domain contexts");
    }

    [Fact]
    public void Features_ShouldNotDependOnDbContext()
    {
        var violations = FeatureFiles()
            .Where(file => ContainsAny(File.ReadAllText(file), "DbContext", "DbReadContext", "DbWriteContext"))
            .Select(Path.GetFileName)
            .ToList();

        violations.ShouldBeEmpty("Features must read and write only through split Domain contexts");
    }

    [Fact]
    public void Modules_ShouldNotDefineOrUseCombinedDomainContext()
    {
        var violations = ModuleFiles()
            .Where(file => ContainsAny(
                File.ReadAllText(file),
                "AgentsDomainContext",
                "AuditDomainContext",
                "IdentityDomainContext",
                "NotificationDomainContext",
                "SearchDomainContext",
                "VaultDomainContext"))
            .Select(Path.GetFileName)
            .ToList();

        violations.ShouldBeEmpty("read and write persistence must use separate DomainReadContext and DomainWriteContext types");
    }

    private static IEnumerable<Type> ConsumerTypes() =>
        ModuleAssemblies
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false }
                           && type.GetInterfaces().Any(IsConsumerInterface));

    private static bool IsConsumerInterface(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IConsumer<>);

    private static bool HasNamespaceSegment(Type type, string segment) =>
        type.Namespace?.Split('.').Contains(segment, StringComparer.Ordinal) == true;

    private static IEnumerable<string> FeatureFiles()
        => ModuleFiles()
            .Where(file => file.Split(Path.DirectorySeparatorChar).Contains("Features", StringComparer.Ordinal));

    private static IEnumerable<string> ApplicationFiles()
        => ModuleFiles()
            .Where(file =>
            {
                var segments = file.Split(Path.DirectorySeparatorChar);
                return segments.Contains("Features", StringComparer.Ordinal)
                       || segments.Contains("Triggers", StringComparer.Ordinal);
            });

    private static IEnumerable<string> ModuleFiles()
    {
        var root = FindRepositoryRoot();
        var modules = Path.Combine(root, "src", "modules");

        return Directory.EnumerateFiles(modules, "*.cs", SearchOption.AllDirectories);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Palladin.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new DirectoryNotFoundException("Could not locate the backend repository root.");
    }

    private static bool ContainsAny(string source, params string[] forbidden) =>
        forbidden.Any(value => source.Contains(value, StringComparison.Ordinal));
}
