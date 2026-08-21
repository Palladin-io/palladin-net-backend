using System.Collections;
using System.Reflection;
using FastEndpoints;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Palladin.Core.Security;
using Palladin.Module.Agents.Infrastructure.AgentAuth;
using Palladin.Module.PublicAssetCatalog.Infrastructure;
using Palladin.Tests.Integrations.Shared;
using Shouldly;

namespace Palladin.Tests.Integrations.Architecture;

[Collection<ApiFactoryCollection>]
public sealed class EndpointSecurityBoundaryTests(ApiFactory apiFactory) : TestBase
{
    private static readonly HashSet<string> SupportedAuthenticationSchemes =
    [
        JwtBearerDefaults.AuthenticationScheme,
        AgentAuthenticationOptions.SchemeName,
        PublicAssetServiceAuthentication.Scheme,
    ];

    [Fact]
    public void EveryConcreteEndpoint_ShouldHaveExactlyOneEffectiveSecurityBoundary()
    {
        // Given — use the definitions built by FastEndpoints itself. This includes
        // configuration inherited from custom endpoint base classes.
        var definitions = apiFactory.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .Select(endpoint => endpoint.Metadata.GetMetadata<EndpointDefinition>())
            .OfType<EndpointDefinition>()
            .GroupBy(definition => ReadRequiredProperty<Type>(definition, "EndpointType"))
            .ToDictionary(group => group.Key, group => group.First());

        var concreteEndpointTypes = Directory
            .EnumerateFiles(AppContext.BaseDirectory, "Palladin.*.dll")
            .Select(AssemblyName.GetAssemblyName)
            .Select(Assembly.Load)
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false }
                           && typeof(IEndpoint).IsAssignableFrom(type))
            .ToHashSet();

        // Then — every concrete endpoint discovered in the loaded endpoint assemblies
        // must have a runtime definition, including indirect Endpoint descendants.
        definitions.Keys
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ShouldBe(concreteEndpointTypes.OrderBy(type => type.FullName, StringComparer.Ordinal));

        var violations = definitions
            .Select(pair => Describe(pair.Key, pair.Value))
            .Select(descriptor => (Descriptor: descriptor, Violation: FindViolation(descriptor)))
            .Where(result => result.Violation is not null)
            .Select(result => $"{result.Descriptor.EndpointType.FullName}: {result.Violation}")
            .Order(StringComparer.Ordinal)
            .ToList();

        violations.ShouldBeEmpty(
            "every FastEndpoints endpoint must fail closed behind one effective authenticated/authorized boundary or be explicitly anonymous");
    }

    [Theory]
    [MemberData(nameof(BoundaryCases))]
    public void BoundaryClassifier_ShouldRejectUnsafeCombinations(
        EndpointBoundaryDescriptor descriptor,
        bool expectedValid)
    {
        (FindViolation(descriptor) is null).ShouldBe(expectedValid);
    }

    public static TheoryData<EndpointBoundaryDescriptor, bool> BoundaryCases() => new()
    {
        { Descriptor(schemes: [JwtBearerDefaults.AuthenticationScheme]), true },
        { Descriptor(schemes: [AgentAuthenticationOptions.SchemeName]), true },
        { Descriptor(schemes: [PublicAssetServiceAuthentication.Scheme]), true },
        { Descriptor(hasRoles: true), true },
        { Descriptor(hasPermissions: true), true },
        { Descriptor(requiredPermissions: [Permission.VaultManage]), true },
        { Descriptor(verbs: ["GET"], anonymousVerbs: ["GET"]), true },
        { Descriptor(), false },
        { Descriptor(schemes: [JwtBearerDefaults.AuthenticationScheme, AgentAuthenticationOptions.SchemeName]), false },
        { Descriptor(schemes: ["Unsupported"]), false },
        { Descriptor(requiredPermissions: [Permission.None]), false },
        { Descriptor(verbs: ["GET", "POST"], anonymousVerbs: ["GET"]), false },
        { Descriptor(verbs: ["GET"], anonymousVerbs: ["get"]), false },
        { Descriptor(schemes: [JwtBearerDefaults.AuthenticationScheme], verbs: ["GET"], anonymousVerbs: ["GET"]), false },
        { Descriptor(hasPermissions: true, verbs: ["GET"], anonymousVerbs: ["GET"]), false },
    };

    private static EndpointBoundaryDescriptor Describe(Type endpointType, EndpointDefinition definition)
    {
        var schemes = ReadCollection(definition, "AuthSchemeNames")
            .OfType<string>()
            .ToArray();
        var verbs = ReadCollection(definition, "Verbs")
            .OfType<string>()
            .ToArray();
        var anonymousVerbs = ReadCollection(definition, "AnonymousVerbs")
            .OfType<string>()
            .ToArray();
        var roles = ReadCollection(definition, "AllowedRoles");
        var permissions = ReadCollection(definition, "AllowedPermissions");
        var preProcessors = ReadCollection(definition, "PreProcessorsList");
        var requiredPermissions = preProcessors
            .Where(processor => IsPermissionPreProcessor(processor.GetType()))
            .Select(processor => ReadRequiredProperty<Permission>(processor, "RequiredPermission"))
            .ToArray();

        return new EndpointBoundaryDescriptor(
            endpointType,
            schemes,
            verbs,
            anonymousVerbs,
            roles.Count > 0,
            permissions.Count > 0,
            requiredPermissions);
    }

    private static string? FindViolation(EndpointBoundaryDescriptor descriptor)
    {
        var hasSupportedScheme = descriptor.AuthenticationSchemes.Count == 1
                                 && SupportedAuthenticationSchemes.Contains(
                                     descriptor.AuthenticationSchemes[0]);
        var hasInvalidPermissionPreProcessor = descriptor.RequiredPermissions
            .Any(permission => permission == Permission.None);
        var hasAuthorization = descriptor.HasRoles
                               || descriptor.HasPermissions
                               || descriptor.RequiredPermissions.Count > 0;
        var verbs = descriptor.Verbs.ToHashSet(StringComparer.Ordinal);
        var anonymousVerbs = descriptor.AnonymousVerbs.ToHashSet(StringComparer.Ordinal);
        var isAnonymous = verbs.Count > 0 && verbs.IsSubsetOf(anonymousVerbs);
        var hasPartialAnonymousBoundary = verbs.Overlaps(anonymousVerbs) && !isAnonymous;

        if (hasPartialAnonymousBoundary)
        {
            return $"only some endpoint verbs are configured as anonymous "
                   + $"(verbs: {string.Join(',', verbs)}; anonymous: {string.Join(',', anonymousVerbs)})";
        }

        if (isAnonymous)
        {
            return descriptor.AuthenticationSchemes.Count == 0 && !hasAuthorization
                ? null
                : "anonymous boundary is combined with authenticated authorization";
        }

        if (hasInvalidPermissionPreProcessor)
        {
            return "RequirePermission is configured with Permission.None";
        }

        if (descriptor.AuthenticationSchemes.Count > 1)
        {
            return "multiple authentication schemes are configured";
        }

        if (descriptor.AuthenticationSchemes.Count == 1 && !hasSupportedScheme)
        {
            return $"unsupported authentication scheme '{descriptor.AuthenticationSchemes[0]}'";
        }

        return hasSupportedScheme || hasAuthorization
            ? null
            : "no explicit authenticated or authorization boundary";
    }

    private static bool IsPermissionPreProcessor(Type type) =>
        type.IsGenericType
        && type.GetGenericTypeDefinition() == typeof(RequirePermissionPreProcessor<>);

    private static IReadOnlyList<object> ReadCollection(object source, string propertyName)
    {
        var value = ReadRequiredProperty<object?>(source, propertyName);
        return value is IEnumerable enumerable
            ? enumerable.Cast<object>().ToArray()
            : [];
    }

    private static T ReadRequiredProperty<T>(object source, string propertyName)
    {
        var property = source.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        property.ShouldNotBeNull($"FastEndpoints EndpointDefinition.{propertyName} must remain inspectable");
        return (T)property.GetValue(source)!;
    }

    private static EndpointBoundaryDescriptor Descriptor(
        IReadOnlyList<string>? schemes = null,
        IReadOnlyList<string>? verbs = null,
        IReadOnlyList<string>? anonymousVerbs = null,
        bool hasRoles = false,
        bool hasPermissions = false,
        IReadOnlyList<Permission>? requiredPermissions = null) =>
        new(
            typeof(EndpointSecurityBoundaryTests),
            schemes ?? [],
            verbs ?? ["GET"],
            anonymousVerbs ?? [],
            hasRoles,
            hasPermissions,
            requiredPermissions ?? []);

    public sealed record EndpointBoundaryDescriptor(
        Type EndpointType,
        IReadOnlyList<string> AuthenticationSchemes,
        IReadOnlyList<string> Verbs,
        IReadOnlyList<string> AnonymousVerbs,
        bool HasRoles,
        bool HasPermissions,
        IReadOnlyList<Permission> RequiredPermissions);
}
