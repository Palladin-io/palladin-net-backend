using System.Reflection;
using System.Text.RegularExpressions;
using Palladin.Core.Events;
using Palladin.Module.Agents;
using Palladin.Module.Audit;
using Palladin.Module.Identity;
using Palladin.Module.Notification;
using Palladin.Module.PublicAssetCatalog;
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
        typeof(PublicAssetCatalogModule).Assembly,
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
    public void ConsumerEndpoints_ShouldFollowModuleTypeDestinationConvention()
    {
        var violations = ConsumerTypes()
            .Select(ValidateEndpointName)
            .Where(violation => violation is not null)
            .ToList();

        violations.ShouldBeEmpty(
            "queue names must follow {module}.{events|commands}.{destination}; event destinations identify their source module");
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

    private static string? ValidateEndpointName(Type consumerType)
    {
        var definitionType = consumerType.Assembly.GetTypes()
            .SingleOrDefault(type => DefinesConsumer(type, consumerType));
        if (definitionType is null)
        {
            return $"{consumerType.FullName}: missing ConsumerDefinition";
        }

        var definition = Activator.CreateInstance(definitionType, nonPublic: true);
        var endpointName = ReadMember(definitionType, definition, "EndpointName", "_endpointName") as string;
        if (string.IsNullOrWhiteSpace(endpointName))
        {
            return $"{consumerType.FullName}: missing EndpointName";
        }

        var messageType = consumerType.GetInterfaces()
            .Single(IsConsumerInterface)
            .GetGenericArguments()[0];
        var receiver = GetModuleName(consumerType.Assembly);

        if (typeof(IIntegrationCommand).IsAssignableFrom(messageType))
        {
            var commandPattern = $"^{Regex.Escape(receiver)}\\.commands\\.[a-z0-9]+(?:-[a-z0-9]+)*$";
            return Regex.IsMatch(endpointName, commandPattern, RegexOptions.CultureInvariant)
                ? null
                : $"{consumerType.FullName}: {endpointName}";
        }

        var eventType = UnwrapFault(messageType);
        if (!typeof(IIntegrationEvent).IsAssignableFrom(eventType) && eventType == messageType)
        {
            return $"{consumerType.FullName}: unsupported message type {messageType.FullName}";
        }

        var source = GetModuleName(eventType.Assembly);
        var destination = source == receiver ? "self" : source;
        var expected = $"{receiver}.events.{destination}";

        return endpointName == expected
            ? null
            : $"{consumerType.FullName}: {endpointName} (expected {expected})";
    }

    private static bool DefinesConsumer(Type definitionType, Type consumerType)
    {
        for (var type = definitionType; type is not null; type = type.BaseType)
        {
            if (type.IsGenericType
                && type.GetGenericTypeDefinition() == typeof(ConsumerDefinition<>)
                && type.GetGenericArguments()[0] == consumerType)
            {
                return true;
            }
        }

        return false;
    }

    private static object? ReadMember(Type type, object? instance, string propertyName, string fieldName)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var property = current.GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (property?.GetMethod is not null)
            {
                return property.GetValue(instance);
            }

            var field = current.GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field is not null)
            {
                return field.GetValue(instance);
            }
        }

        return null;
    }

    private static Type UnwrapFault(Type messageType) =>
        messageType.IsGenericType && messageType.GetGenericTypeDefinition() == typeof(Fault<>)
            ? messageType.GetGenericArguments()[0]
            : messageType;

    private static string GetModuleName(Assembly assembly)
    {
        const string prefix = "Palladin.Module.";
        var assemblyName = assembly.GetName().Name!;
        var moduleName = assemblyName[prefix.Length..].Split('.')[0];

        return Regex.Replace(moduleName, "(?<!^)([A-Z])", "-$1", RegexOptions.CultureInvariant)
            .ToLowerInvariant();
    }

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
