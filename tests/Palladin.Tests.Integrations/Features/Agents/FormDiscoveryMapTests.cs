using System.Net;
using FastEndpoints;
using FastEndpoints.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NodaTime;
using Palladin.Core.Types;
using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Features;
using Palladin.Module.Agents.Infrastructure.DiscoveryMaps;
using Palladin.Module.Agents.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Agents;

[Collection<ApiFactoryCollection>]
public sealed class FormDiscoveryMapTests(ApiFactory apiFactory) : TestBase
{
    private static readonly FormDiscoveryMapDefinition SafeDefinition = new(
        1,
        new FormDiscoveryFormDefinition(
            1,
            [
                new FormDiscoveryStepDefinition(
                    [
                        new FormDiscoveryFieldDefinition(
                            "credential.password",
                            "input[type=password]",
                            "password"),
                    ],
                    new FormDiscoverySubmitDefinition(
                        "click",
                        "button[type=submit]")),
            ]));

    [Fact]
    public async Task When_TwoGlobalCandidatesCommitConcurrently_Then_DatabaseAssignsDistinctRevisions()
    {
        // Given
        var firstDomain = $"race-first-{Guid.NewGuid():N}.example.com";
        var secondDomain = $"race-second-{Guid.NewGuid():N}.example.com";
        const string provider = "example";
        await using var firstScope = apiFactory.Services.CreateAsyncScope();
        await using var secondScope = apiFactory.Services.CreateAsyncScope();
        var firstContext = firstScope.ServiceProvider.GetRequiredService<AgentsDomainWriteContext>();
        var secondContext = secondScope.ServiceProvider.GetRequiredService<AgentsDomainWriteContext>();
        var first = CreateCandidate(Contract, firstDomain, provider);
        var second = CreateCandidate(Contract, secondDomain, provider);
        firstContext.Add(first);
        secondContext.Add(second);

        // When
        await Task.WhenAll(
            firstContext.CommitAsync(TestContext.Current.CancellationToken),
            secondContext.CommitAsync(TestContext.Current.CancellationToken));

        // Then
        first.MapVersion.ShouldBeGreaterThan(0);
        second.MapVersion.ShouldBeGreaterThan(0);
        second.MapVersion.ShouldNotBe(first.MapVersion);
    }

    [Fact]
    public async Task When_AgentSubmitsSameGlobalCandidateConcurrently_Then_SubmitIsIdempotentAndPreservesProvenance()
    {
        // Given
        var (_, organization, _) = await apiFactory.Services.SeedUserAsync();
        var (_, plaintext) = await apiFactory.Services.SeedApiKeyAsync(organization.Id);
        var publicKey = AgentFaker.GeneratePublicKey();
        var signing = AgentRequestSigning.Generate();
        var agent = await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(
                    organizationId: organization.Id,
                    publicKey: publicKey,
                    signingPublicKey: signing.PublicKeyBase64)
                .RuleFor(candidate => candidate.Status, AgentStatus.Active));
        var domain = $"submit-{Guid.NewGuid():N}.example.net";
        var validated = Validate(
            Contract,
            domain,
            $"https://{domain}/pl/zaloguj-sie",
            "  Custom-Browser-42  ");
        var request = new SubmitFormDiscoveryMapRequest(
            domain,
            validated.LoginUrl,
            "  Custom-Browser-42  ",
            Contract.ComputeFingerprint(validated),
            SafeDefinition);
        var client = apiFactory.CreateSignedAgentClient(agent.Id, plaintext, publicKey, signing);

        // When
        var submissions = await Task.WhenAll(
            client.POSTAsync<SubmitFormDiscoveryMapEndpoint, SubmitFormDiscoveryMapRequest, SubmitFormDiscoveryMapResponse>(
                request),
            client.POSTAsync<SubmitFormDiscoveryMapEndpoint, SubmitFormDiscoveryMapRequest, SubmitFormDiscoveryMapResponse>(
                request));
        var (response, result) = submissions[0];
        var (duplicateResponse, duplicateResult) = submissions[1];

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        duplicateResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.Status.ShouldBe("candidate");
        duplicateResult.MapId.ShouldBe(result.MapId);
        duplicateResult.MapVersion.ShouldBe(result.MapVersion);
        duplicateResult.Status.ShouldBe(result.Status);
        await using var assertScope = apiFactory.Services.CreateAsyncScope();
        var readContext = assertScope.ServiceProvider.GetRequiredService<AgentsDomainReadContext>();
        var stored = await readContext.FormDiscoveryMaps.SingleAsync(
            map => map.Id == result.MapId,
            TestContext.Current.CancellationToken);
        var matchingCount = await readContext.FormDiscoveryMaps.CountAsync(
            map => map.Domain == domain
                && map.Provider == "custom-browser-42"
                && map.Fingerprint == request.Fingerprint,
            TestContext.Current.CancellationToken);
        matchingCount.ShouldBe(1);
        stored.SubmittedByAgentId.ShouldBe(agent.Id);
        stored.Domain.ShouldBe(domain);
        stored.LoginUrl.ShouldBe(validated.LoginUrl);
        stored.Provider.ShouldBe("custom-browser-42");
        stored.Status.ShouldBe(FormDiscoveryMapStatus.Candidate);
    }

    [Fact]
    public async Task When_GlobalCandidateIsNotVerified_Then_LookupReturnsOnlyVerifiedRevision()
    {
        // Given
        var domain = $"global-{Guid.NewGuid():N}.example.net";
        var now = SystemClock.Instance.GetCurrentInstant();
        var verified = CreateCandidate(Contract, domain, "playwright", now: now);
        verified.MarkVerified(now.Plus(Duration.FromSeconds(1)));
        var candidate = CreateCandidate(
            Contract,
            domain,
            "playwright",
            new string('c', 64),
            now: now.Plus(Duration.FromSeconds(2)));
        await using var seedScope = apiFactory.Services.CreateAsyncScope();
        var writeContext = seedScope.ServiceProvider.GetRequiredService<AgentsDomainWriteContext>();
        writeContext.Add(verified);
        writeContext.Add(candidate);
        await writeContext.CommitAsync(TestContext.Current.CancellationToken);

        // When
        await using var lookupScope = apiFactory.Services.CreateAsyncScope();
        var readContext = lookupScope.ServiceProvider.GetRequiredService<AgentsDomainReadContext>();
        var result = await Contract.FindVerifiedAsync(
            readContext.FormDiscoveryMaps,
            domain,
            "playwright",
            TestContext.Current.CancellationToken);

        // Then
        result.ShouldNotBeNull().Map.Id.ShouldBe(verified.Id);
    }

    [Fact]
    public async Task When_NewerVerifiedRowsFailPublicationContract_Then_LookupFallsBackToOlderSafeRevision()
    {
        // Given
        var now = SystemClock.Instance.GetCurrentInstant();
        var domain = $"fallback-{Guid.NewGuid():N}.example.net";
        var safe = CreateCandidate(Contract, domain, "playwright", now: now);
        safe.MarkVerified(now.Plus(Duration.FromSeconds(1)));
        var mismatched = CreateCandidate(
            Contract,
            domain,
            "playwright",
            new string('a', 64),
            now: now.Plus(Duration.FromSeconds(2)));
        mismatched.MarkVerified(now.Plus(Duration.FromSeconds(3)));
        var malformed = FormDiscoveryMap.CreateCandidate(
            Guid.NewGuid(),
            Guid.NewGuid(),
            domain,
            $"https://{domain}/login",
            "playwright",
            new string('b', 64),
            "{not-json",
            now.Plus(Duration.FromSeconds(4)));
        malformed.MarkVerified(now.Plus(Duration.FromSeconds(5)));
        await using var seedScope = apiFactory.Services.CreateAsyncScope();
        var writeContext = seedScope.ServiceProvider.GetRequiredService<AgentsDomainWriteContext>();
        writeContext.Add(safe);
        await writeContext.CommitAsync(TestContext.Current.CancellationToken);
        writeContext.Add(mismatched);
        await writeContext.CommitAsync(TestContext.Current.CancellationToken);
        writeContext.Add(malformed);
        await writeContext.CommitAsync(TestContext.Current.CancellationToken);

        // When
        await using var lookupScope = apiFactory.Services.CreateAsyncScope();
        var readContext = lookupScope.ServiceProvider.GetRequiredService<AgentsDomainReadContext>();
        var result = await Contract.FindVerifiedAsync(
            readContext.FormDiscoveryMaps,
            domain,
            "playwright",
            TestContext.Current.CancellationToken);

        // Then
        result.ShouldNotBeNull().Map.Id.ShouldBe(safe.Id);
    }

    [Theory]
    [InlineData("https://login.example.org/login", "example.org")]
    [InlineData("http://example.org/login", "example.org")]
    [InlineData("https://example.org/login#credential", "example.org")]
    [InlineData("https://example.org/login#", "example.org")]
    [InlineData("https://example.org/login?access_token=secret", "example.org")]
    [InlineData("https://example.org/login?", "example.org")]
    [InlineData("https://user@example.org/login", "example.org")]
    [InlineData("https://example.org:8443/login", "example.org")]
    public void When_LoginUrlViolatesGenericOriginRules_Then_MapIsUnsafe(
        string loginUrl,
        string domain)
    {
        // Given
        var contract = Contract;

        // When
        var result = contract.TryValidate(
            domain,
            loginUrl,
            "playwright",
            SafeDefinition,
            out _);

        // Then
        result.ShouldBeFalse();
    }

    [Theory]
    [InlineData("https://example.org/")]
    [InlineData("https://example.org/reset/one-time-token")]
    [InlineData("https://example.org/pl/zaloguj-sie")]
    [InlineData("https://example.org/%D8%AA%D8%B3%D8%AC%D9%8A%D9%84-%D8%A7%D9%84%D8%AF%D8%AE%D9%88%D9%84")]
    public void When_LoginUrlUsesAnyQueryFreePath_Then_MapIsSafe(string loginUrl)
    {
        // Given
        var contract = Contract;

        // When
        var result = contract.TryValidate(
            "example.org",
            loginUrl,
            "playwright",
            SafeDefinition,
            out _);

        // Then
        result.ShouldBeTrue();
    }

    [Fact]
    public void When_DomainIsUnicode_Then_DomainAndLoginHostUseTheSamePunycodeForm()
    {
        // When
        var result = Contract.TryValidate(
            "münchen.de",
            "https://münchen.de/anmelden",
            "playwright",
            SafeDefinition,
            out var validated);

        // Then
        result.ShouldBeTrue();
        validated.Domain.ShouldBe("xn--mnchen-3ya.de");
        new Uri(validated.LoginUrl).IdnHost.ShouldBe(validated.Domain);
    }

    [Fact]
    public void When_OptionalOverlayListIsEmpty_Then_TypedWireShapeOmitsIt()
    {
        // Given
        var withEmptyOverlays = SafeDefinition with
        {
            CookieOverlays = [],
        };

        // When
        var empty = Validate(
            Contract,
            "example.org",
            "https://example.org/login",
            "playwright",
            withEmptyOverlays);
        var absent = Validate(
            Contract,
            "example.org",
            "https://example.org/login",
            "playwright");

        // Then
        empty.Definition.CookieOverlays.ShouldBeNull();
        empty.DefinitionJson.ShouldNotContain("cookieOverlays");
        Contract.ComputeFingerprint(empty).ShouldBe(Contract.ComputeFingerprint(absent));
    }

    [Fact]
    public void When_DefinitionContainsAnUnknownProperty_Then_TypedContractRejectsIt()
    {
        // Given
        const string definition = """
            {
              "version": 1,
              "cookieValue": "secret",
              "form": {
                "version": 1,
                "steps": [{
                  "fields": [{
                    "entryFieldId": "credential.password",
                    "selector": "input[type=password]",
                    "control": "password"
                  }],
                  "submit": { "action": "click", "selector": "button[type=submit]" }
                }]
              }
            }
            """;

        // When
        var result = Contract.TryDeserializeDefinition(definition, out _);

        // Then
        result.ShouldBeFalse();
    }

    [Theory]
    [InlineData("private.credit-card-number", "password")]
    [InlineData("custom:123e4567-e89b-12d3-a456-426614174000", "text")]
    [InlineData("credential.totp", "otp")]
    public void When_FieldUsesSchemaIdentifierAndSupportedControl_Then_MapIsLanguageIndependent(
        string fieldId,
        string control)
    {
        // Given
        var definition = DefinitionWithField(
            fieldId,
            "input[aria-label='Zaloguj się']",
            control);

        // When
        var result = Contract.TryValidate(
            "example.org",
            "https://example.org/pl/zaloguj-sie",
            "playwright",
            definition,
            out _);

        // Then
        result.ShouldBeTrue();
    }

    [Theory]
    [InlineData("field id", "text")]
    [InlineData("credential.username", "select")]
    [InlineData("credential/password", "password")]
    public void When_FieldIdentifierOrControlIsOutsideInjectSchema_Then_MapIsUnsafe(
        string fieldId,
        string control)
    {
        // Given
        var definition = DefinitionWithField(fieldId, "input", control);

        // When
        var result = Contract.TryValidate(
            "example.org",
            "https://example.org/login",
            "playwright",
            definition,
            out _);

        // Then
        result.ShouldBeFalse();
    }

    [Fact]
    public void When_ProviderIsAnOpenSafeIdentifier_Then_ItIsNormalizedWithoutAnAllowlist()
    {
        // Given
        var contract = Contract;

        // When
        var accepted = contract.TryNormalizeProvider("  My-New-Browser-42  ", out var normalized);
        var rejected = contract.TryNormalizeProvider("browser/provider", out _);

        // Then
        accepted.ShouldBeTrue();
        normalized.ShouldBe("my-new-browser-42");
        rejected.ShouldBeFalse();
    }

    [Fact]
    public void When_SelectorExceedsConfiguredUtf8Limit_Then_MapIsUnsafe()
    {
        // Given
        var contract = new FormDiscoveryMapContract(Options.Create(new FormDiscoveryMapOptions
        {
            MaximumSelectorBytes = 8,
        }));
        var definition = DefinitionWithField(
            "credential.password",
            string.Concat(Enumerable.Repeat("😀", 3)),
            "password");

        // When
        var result = contract.TryValidate(
            "example.org",
            "https://example.org/login",
            "playwright",
            definition,
            out _);

        // Then
        result.ShouldBeFalse();
    }

    [Fact]
    public void When_FingerprintMatchesTypedCrossRuntimePayload_Then_ItIsAccepted()
    {
        // Given
        var definition = DefinitionWithField(
            "credential.password",
            "input[aria-label=\"Hasło użytkownika\"]",
            "password") with
        {
            CookieOverlays =
            [
                new FormDiscoveryCookieOverlay(
                    ["[data-testid=cmp]"],
                    new FormDiscoveryOverlayDismiss(
                        "button[data-action=accept]",
                        "click"),
                    "[data-testid=cmp]",
                    "same-origin"),
            ],
        };
        var validated = Validate(
            Contract,
            "example.org",
            "https://example.org/pl/zaloguj-się",
            "custom-browser",
            definition);
        const string fingerprint = "48807755c6780b76aa7842675e59dccdecd1aab96874c7979078ac489d934e9a";

        // When
        var accepted = Contract.FingerprintMatches(validated, fingerprint);
        var changedProvider = Validate(
            Contract,
            "example.org",
            "https://example.org/pl/zaloguj-się",
            "different-browser",
            definition);

        // Then
        accepted.ShouldBeTrue();
        Contract.FingerprintMatches(changedProvider, fingerprint).ShouldBeFalse();
    }

    private FormDiscoveryMapContract Contract =>
        apiFactory.Services.GetRequiredService<FormDiscoveryMapContract>();

    private static FormDiscoveryMap CreateCandidate(
        FormDiscoveryMapContract contract,
        string domain,
        string provider,
        string? fingerprint = null,
        Guid? agentId = null,
        Instant? now = null)
    {
        var validated = Validate(contract, domain, $"https://{domain}/login", provider);
        return FormDiscoveryMap.CreateCandidate(
            Guid.NewGuid(),
            agentId ?? Guid.NewGuid(),
            validated.Domain,
            validated.LoginUrl,
            validated.Provider,
            fingerprint ?? contract.ComputeFingerprint(validated),
            validated.DefinitionJson,
            now ?? SystemClock.Instance.GetCurrentInstant());
    }

    private static ValidatedFormDiscoveryMap Validate(
        FormDiscoveryMapContract contract,
        string domain,
        string loginUrl,
        string provider,
        FormDiscoveryMapDefinition? definition = null)
    {
        contract.TryValidate(
                domain,
                loginUrl,
                provider,
                definition ?? SafeDefinition,
                out var validated)
            .ShouldBeTrue();
        return validated;
    }

    private static FormDiscoveryMapDefinition DefinitionWithField(
        string fieldId,
        string selector,
        string control) => new(
        1,
        new FormDiscoveryFormDefinition(
            1,
            [
                new FormDiscoveryStepDefinition(
                    [new FormDiscoveryFieldDefinition(fieldId, selector, control)],
                    new FormDiscoverySubmitDefinition("click", "button[type=submit]")),
            ]));
}
