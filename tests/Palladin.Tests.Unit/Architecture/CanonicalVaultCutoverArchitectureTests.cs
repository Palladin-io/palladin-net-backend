using System.Text.RegularExpressions;
using Palladin.Core.Events;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Contracts.Commands;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Features;

namespace Palladin.Tests.Unit.Architecture;

public sealed partial class CanonicalVaultCutoverArchitectureTests
{
    private static readonly HashSet<string> SharingRecipientEndpoints =
    [
        nameof(OpenEntryShareSessionEndpoint),
        nameof(VerifyEntryShareSecretEndpoint),
        nameof(DeliverEntryShareEndpoint),
        nameof(ConfirmEntryShareReceiptEndpoint),
        nameof(EndEntryShareEndpoint),
    ];

    [Fact]
    public void AgentCredentialDeliveryResponses_ShouldNeverExposePlaintextDiscoveryMetadata()
    {
        var forbidden = new[] { "Label", "UrlDomain" };

        typeof(DeliverCredentialResponse).GetProperties().Select(x => x.Name)
            .ShouldNotContain(x => forbidden.Contains(x));
        typeof(GetOrRequestCredentialResponse).GetProperties().Select(x => x.Name)
            .ShouldNotContain(x => forbidden.Contains(x));
    }

    [Fact]
    public void VaultRuntime_ShouldNeverIntroduceParallelVersionTwoTypesTablesOrRoutes()
    {
        // Given
        var root = FindRepositoryRoot();
        var sourceFiles = EnumerateVaultRuntimeFiles(root);

        // When
        var violations = sourceFiles
            .SelectMany(file => ForbiddenParallelRuntimePattern()
                .Matches(File.ReadAllText(file))
                .Where(match => !match.Value.StartsWith("PLDN", StringComparison.Ordinal))
                .Select(match => $"{Path.GetRelativePath(root, file)}: {match.Value}"))
            .ToList();

        // Then
        violations.ShouldBeEmpty(
            "protocolVersion=2 is a wire/AAD discriminator and Palladin has one canonical pre-production Vault runtime");
    }

    [Fact]
    public void VaultRuntime_ShouldNeverReadIdentityPersistenceDirectly()
    {
        // Given
        var root = FindRepositoryRoot();
        var sourceFiles = EnumerateVaultRuntimeFiles(root);

        // When
        var violations = sourceFiles
            .SelectMany(file => ForbiddenIdentityPersistencePattern()
                .Matches(File.ReadAllText(file))
                .Select(match => $"{Path.GetRelativePath(root, file)}: {match.Value}"))
            .ToList();

        // Then
        violations.ShouldBeEmpty(
            "Vault creation must read its command-fed local key directory and never perform a live Identity persistence or request/response query");
    }

    [Fact]
    public void VaultOpenHostCommands_ShouldUseTheCommandMarker()
    {
        // Given
        var commands = typeof(UpsertMemberKeyDirectoryCommand).Assembly.GetTypes()
            .Where(type => type.Name.EndsWith("Command", StringComparison.Ordinal))
            .ToList();

        // Then
        commands.ShouldNotBeEmpty();
        commands.ShouldAllBe(type => typeof(IIntegrationCommand).IsAssignableFrom(type));
        commands.ShouldNotContain(type => typeof(IIntegrationEvent).IsAssignableFrom(type));
    }

    [Fact]
    public void AgentDiscoveryPersistence_ShouldNeverStoreRawKeysOrApiKeyCredentials()
    {
        // Given
        var forbiddenPropertyNames = new[]
        {
            "ApiKey",
            "ApiKeyHash",
            "VaultDataKey",
            "Vdk",
            "PrivateKey",
            "Plaintext",
        };

        // When
        var persistedPropertyNames = typeof(AgentVaultDiscoveryEnvelope)
            .GetProperties(System.Reflection.BindingFlags.Instance
                           | System.Reflection.BindingFlags.Public
                           | System.Reflection.BindingFlags.NonPublic)
            .Select(property => property.Name)
            .ToList();

        // Then
        persistedPropertyNames.ShouldNotContain(name => forbiddenPropertyNames.Contains(name));
        persistedPropertyNames.ShouldContain("AgentWrappedVdk");
    }

    [Fact]
    public void CanonicalEnvelopeAggregates_ShouldNeverExposeLegacySplitPayloadProperties()
    {
        var forbiddenProperties = new Dictionary<Type, string[]>
        {
            [typeof(Vault)] = ["MemberVaultMetadataAlgorithmSuite", "MemberVaultMetadataNonce", "MemberVaultMetadataCiphertext"],
            [typeof(VaultEntry)] = ["MemberIndexAlgorithmSuite", "MemberIndexNonce", "MemberIndexCiphertext", "AgentDiscoveryAlgorithmSuite", "AgentDiscoveryNonce", "AgentDiscoveryCiphertext"],
            [typeof(VaultEntryVersion)] = ["AlgorithmSuite", "MemberSecretNonce", "MemberSecretCiphertext"],
            [typeof(VaultEntryKey)] = ["AlgorithmSuite", "Nonce", "WrappedEntryDekByVk"],
            [typeof(VaultKeyMaterialEnvelope)] = ["AlgorithmSuite", "Nonce", "Ciphertext"],
            [typeof(GrantEntryEnvelope)] = ["AlgorithmSuite", "Nonce", "Ciphertext"],
            [typeof(EncryptedReasonEnvelope)] = ["AlgorithmSuite", "Nonce", "Ciphertext"],
            [typeof(AgentVaultDiscoveryEnvelope)] = ["AlgorithmSuite"],
            [typeof(VaultMemberKeyEnvelope)] = ["AlgorithmSuite"],
        };

        var violations = forbiddenProperties
            .SelectMany(pair => pair.Value
                .Where(name => pair.Key.GetProperty(name,
                    System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic) is not null)
                .Select(name => $"{pair.Key.Name}.{name}"))
            .ToList();

        violations.ShouldBeEmpty(
            "persisted envelopes have one canonical CryptoSuiteId plus EncodedSuitePayload shape");
    }

    [Fact]
    public void EveryVaultEndpoint_ShouldDeclareExactlyOneSupportedAuthenticationBoundary()
    {
        // Given
        var root = FindRepositoryRoot();
        var featureFiles = Directory.EnumerateFiles(
            Path.Combine(root, "src", "modules", "Vault", "Palladin.Module.Vault", "Features"),
            "*.cs",
            SearchOption.AllDirectories);

        // When
        var violations = featureFiles
            .SelectMany(file => EndpointClassPattern().Matches(File.ReadAllText(file))
                .Select(match => new
                {
                    Path = Path.GetRelativePath(root, file),
                    Endpoint = match.Groups["name"].Value,
                    Source = match.Value,
                }))
            .Select(item => new
            {
                item.Path,
                item.Endpoint,
                Authentication = AuthenticationSchemePattern().Matches(item.Source)
                    .Select(match => match.Groups[1].Value)
                    .ToList(),
                AllowsAnonymous = item.Source.Contains("AllowAnonymous(", StringComparison.Ordinal),
                IsGuestSharingPost = item.Source.Contains("Post(\"api/entry-shares/", StringComparison.Ordinal),
            })
            .Where(item => SharingRecipientEndpoints.Contains(item.Endpoint)
                ? item.Authentication.Count != 0 || !item.AllowsAnonymous || !item.IsGuestSharingPost
                : item.Authentication.Count != 1
                           || item.AllowsAnonymous
                           || item.Authentication.Any(scheme => scheme is not "JwtBearerDefaults.AuthenticationScheme"
                               and not "AgentAuthenticationOptions.SchemeName"))
            .Select(item => $"{item.Path} ({item.Endpoint}): auth=[{string.Join(", ", item.Authentication)}], anonymous={item.AllowsAnonymous}")
            .ToList();

        // Then
        violations.ShouldBeEmpty(
            "Vault endpoints require Member/Agent authentication; only the enumerated guest sharing POSTs use their independent bearer and verification gates");
    }

    private static IEnumerable<string> EnumerateVaultRuntimeFiles(string root) =>
        Directory.EnumerateDirectories(Path.Combine(root, "src", "modules", "Vault"), "Palladin.Module.Vault*")
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories));

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

    [GeneratedRegex(
        "\\b[A-Za-z_][A-Za-z0-9_]*V2\\b|(?:name|table):\\s*\"[^\"]*V2\"|\"api/(?:v2/vaults|vaults/v2)",
        RegexOptions.CultureInvariant)]
    private static partial Regex ForbiddenParallelRuntimePattern();

    [GeneratedRegex(
        "Palladin\\.Module\\.Identity\\.Infrastructure\\.Persistence|IdentityDb(?:Read|Write)Context|IMemberKeyDirectorySource|GetMemberKeyDirectoryRecord|IRequestClient|IScopedClientFactory",
        RegexOptions.CultureInvariant)]
    private static partial Regex ForbiddenIdentityPersistencePattern();

    [GeneratedRegex(
        "internal\\s+sealed\\s+class\\s+(?<name>\\w+Endpoint)\\b(?:(?!internal\\s+sealed\\s+class\\s+\\w+Endpoint\\b)[\\s\\S])*?:\\s*Endpoint(?:<|WithoutRequest)(?:(?!internal\\s+sealed\\s+class\\s+\\w+Endpoint\\b)[\\s\\S])*",
        RegexOptions.CultureInvariant)]
    private static partial Regex EndpointClassPattern();

    [GeneratedRegex("AuthSchemes\\(([^)]+)\\)", RegexOptions.CultureInvariant)]
    private static partial Regex AuthenticationSchemePattern();
}
