using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Features;
using Palladin.Module.Agents.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Agents;

[Collection<ApiFactoryCollection>]
public sealed class FormDiscoveryMapTests(ApiFactory apiFactory) : TestBase
{
    private const string SafeDefinition = """
        {
          "version": 1,
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

    [Fact]
    public async Task When_TwoCandidatesForOneProviderRace_Then_MapVersionsRemainMonotonic()
    {
        // Given
        var organizationId = Guid.NewGuid();
        const string domain = "example.com";
        const string provider = "example";
        await using var firstScope = apiFactory.Services.CreateAsyncScope();
        await using var secondScope = apiFactory.Services.CreateAsyncScope();
        var firstContext = firstScope.ServiceProvider.GetRequiredService<AgentsDomainWriteContext>();
        var secondContext = secondScope.ServiceProvider.GetRequiredService<AgentsDomainWriteContext>();
        await using var firstTransaction = await firstContext.BeginTransactionAsync(
            TestContext.Current.CancellationToken);
        var firstVersion = await firstContext.LockAndLoadNextFormDiscoveryMapVersionAsync(
            organizationId,
            domain,
            provider,
            TestContext.Current.CancellationToken);
        await using var secondTransaction = await secondContext.BeginTransactionAsync(
            TestContext.Current.CancellationToken);

        // When
        var secondVersionTask = secondContext.LockAndLoadNextFormDiscoveryMapVersionAsync(
            organizationId,
            domain,
            provider,
            TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        secondVersionTask.IsCompleted.ShouldBeFalse();

        firstContext.Add(CreateCandidate(organizationId, domain, provider, firstVersion));
        await firstContext.CommitAsync(firstTransaction, TestContext.Current.CancellationToken);
        var secondVersion = await secondVersionTask.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        secondContext.Add(CreateCandidate(organizationId, domain, provider, secondVersion));
        await secondContext.CommitAsync(secondTransaction, TestContext.Current.CancellationToken);

        // Then
        firstVersion.ShouldBe(1);
        secondVersion.ShouldBe(2);
        await using var assertScope = apiFactory.Services.CreateAsyncScope();
        var readContext = assertScope.ServiceProvider.GetRequiredService<AgentsDomainReadContext>();
        var versions = await readContext.FormDiscoveryMaps
            .Where(x => x.OrganizationId == organizationId && x.Domain == domain && x.Provider == provider)
            .OrderBy(x => x.MapVersion)
            .Select(x => x.MapVersion)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        versions.ShouldBe([1, 2]);
    }

    [Fact]
    public async Task When_OrganizationMapIsNotVerified_Then_SystemMapWins_UntilOrganizationMapIsVerified()
    {
        // Given
        var organizationId = Guid.NewGuid();
        var now = SystemClock.Instance.GetCurrentInstant();
        var domain = $"map-{Guid.NewGuid():N}.example.net";
        var systemMap = FormDiscoveryMap.CreateSystemVerified(
            Guid.NewGuid(),
            domain,
            $"https://{domain}/login",
            "playwright",
            new string('a', 64),
            SafeDefinition,
            3,
            now);
        var organizationMap = FormDiscoveryMap.CreateCandidate(
            Guid.NewGuid(),
            organizationId,
            Guid.NewGuid(),
            domain,
            $"https://{domain}/sign-in",
            "playwright",
            new string('b', 64),
            SafeDefinition,
            1,
            now.Plus(Duration.FromSeconds(1)));
        await using var seedScope = apiFactory.Services.CreateAsyncScope();
        var writeContext = seedScope.ServiceProvider.GetRequiredService<AgentsDomainWriteContext>();
        writeContext.Add(systemMap);
        writeContext.Add(organizationMap);
        await writeContext.CommitAsync(TestContext.Current.CancellationToken);

        // When
        await using var candidateScope = apiFactory.Services.CreateAsyncScope();
        var candidateReadContext = candidateScope.ServiceProvider.GetRequiredService<AgentsDomainReadContext>();
        var beforeVerification = await FormDiscoveryMapContract.FindVerifiedAsync(
            candidateReadContext.FormDiscoveryMaps,
            organizationId,
            domain,
            "playwright",
            TestContext.Current.CancellationToken);
        organizationMap.MarkVerified(now.Plus(Duration.FromSeconds(2)));
        await writeContext.CommitAsync(TestContext.Current.CancellationToken);
        await using var verifiedScope = apiFactory.Services.CreateAsyncScope();
        var verifiedReadContext = verifiedScope.ServiceProvider.GetRequiredService<AgentsDomainReadContext>();
        var afterVerification = await FormDiscoveryMapContract.FindVerifiedAsync(
            verifiedReadContext.FormDiscoveryMaps,
            organizationId,
            domain,
            "playwright",
            TestContext.Current.CancellationToken);

        // Then
        beforeVerification.ShouldNotBeNull().Scope.ShouldBe(FormDiscoveryMapScope.System);
        afterVerification.ShouldNotBeNull().Scope.ShouldBe(FormDiscoveryMapScope.Organization);
        afterVerification.Id.ShouldBe(organizationMap.Id);
    }

    [Theory]
    [InlineData("https://login.example.org/login", "example.org")]
    [InlineData("http://example.org/login", "example.org")]
    [InlineData("https://example.org/login#credential", "example.org")]
    public void When_LoginOriginIsNotExactHttpsHost_Then_MapIsUnsafe(string loginUrl, string domain)
    {
        using var definition = System.Text.Json.JsonDocument.Parse(SafeDefinition);

        FormDiscoveryMapContract.IsSafe(definition.RootElement, domain, loginUrl).ShouldBeFalse();
    }

    [Fact]
    public void When_DefinitionContainsAnUnknownSecretBearingProperty_Then_MapIsUnsafe()
    {
        using var definition = System.Text.Json.JsonDocument.Parse(
            SafeDefinition.Replace("\"form\":", "\"cookieValue\": \"secret\", \"form\":"));

        FormDiscoveryMapContract.IsSafe(
            definition.RootElement,
            "example.org",
            "https://example.org/login").ShouldBeFalse();
    }

    [Fact]
    public void When_DefinitionMatchesTheBoundedContract_Then_MapIsSafe()
    {
        using var definition = System.Text.Json.JsonDocument.Parse(SafeDefinition);

        FormDiscoveryMapContract.IsSafe(
            definition.RootElement,
            "example.org",
            "https://example.org/login").ShouldBeTrue();
    }

    [Fact]
    public void When_FingerprintMatchesTheCrossRuntimeContract_Then_ItIsAccepted()
    {
        using var definition = System.Text.Json.JsonDocument.Parse(SafeDefinition);
        const string fingerprint = "6767757f95d393326c31593ce4ddc97a63f30821ad806c8c1bc63df59dca5787";

        FormDiscoveryMapContract.FingerprintMatches(
            definition.RootElement,
            "example.org",
            "https://example.org/login",
            fingerprint).ShouldBeTrue();
        FormDiscoveryMapContract.FingerprintMatches(
            definition.RootElement,
            "example.org",
            "https://example.org/login",
            new string('a', 64)).ShouldBeFalse();
    }

    private static FormDiscoveryMap CreateCandidate(
        Guid organizationId,
        string domain,
        string provider,
        int version) =>
        FormDiscoveryMap.CreateCandidate(
            Guid.NewGuid(),
            organizationId,
            Guid.NewGuid(),
            domain,
            "https://example.com/login",
            provider,
            new string('a', 64),
            SafeDefinition,
            version,
            SystemClock.Instance.GetCurrentInstant());
}
