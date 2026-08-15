using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Agents;

[Collection<ApiFactoryCollection>]
public sealed class FormDiscoveryMapTests(ApiFactory apiFactory) : TestBase
{
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
            "{\"version\":1,\"form\":{\"version\":1,\"steps\":[]}}",
            version,
            SystemClock.Instance.GetCurrentInstant());
}
