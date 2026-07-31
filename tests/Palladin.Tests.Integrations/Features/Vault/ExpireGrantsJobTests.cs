using Palladin.Core.Types;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Seeders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

[Collection<ApiFactoryCollection>]
public sealed class ExpireGrantsJobTests(ApiFactory apiFactory) : TestBase
{
    private async Task RunJobAsync()
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var job = scope.ServiceProvider.GetRequiredService<ExpireGrantsJob>();
        await job.ExecuteAsync(CancellationToken.None);
    }

    private async Task<(Guid VaultId, Guid OrgId, Guid AgentId, Guid UserId)> SetupAsync()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var agent = await apiFactory.Services.SeedVaultAgentAsync(organization.Id);
        return (vault.Id, organization.Id, agent.Id, user.Id);
    }

    [Fact]
    public async Task When_TimeBasedGrantExpired_Then_TransitionsToExpired()
    {
        // Given
        var (vaultId, orgId, agentId, userId) = await SetupAsync();
        var now = apiFactory.FakeClock.GetCurrentInstant();
        var grant = await apiFactory.Services.SeedGranularGrantAsync(
            GrantFaker.CreateGranular(vaultId: vaultId, organizationId: orgId, agentId: agentId, createdBy: userId)
                .RuleFor(x => x.ExpiresAt, now.Minus(Duration.FromMinutes(5))).Generate());

        // When
        await RunJobAsync();

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var persisted = await readContext.Grants.FirstAsync(g => g.Id == grant.Id);
        persisted.Status.ShouldBe(GrantStatus.Expired);
    }

    [Fact]
    public async Task When_ActiveGrantNotYetExpired_Then_LeftUnchanged()
    {
        // Given
        var (vaultId, orgId, agentId, userId) = await SetupAsync();
        var now = apiFactory.FakeClock.GetCurrentInstant();
        var grant = await apiFactory.Services.SeedGranularGrantAsync(
            GrantFaker.CreateGranular(vaultId: vaultId, organizationId: orgId, agentId: agentId, createdBy: userId)
                .RuleFor(x => x.ExpiresAt, now.Plus(Duration.FromHours(1))).Generate());

        // When
        await RunJobAsync();

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var persisted = await readContext.Grants.FirstAsync(g => g.Id == grant.Id);
        persisted.Status.ShouldBe(GrantStatus.Active);
    }

    [Fact]
    public async Task When_UseBasedGrant_Then_NotExpiredByCron()
    {
        // Given — use-based grant: QueryLimit set, ExpiresAt null
        var (vaultId, orgId, agentId, userId) = await SetupAsync();
        var grant = await apiFactory.Services.SeedGranularGrantAsync(
            GrantFaker.CreateGranular(vaultId: vaultId, organizationId: orgId, agentId: agentId, createdBy: userId)
                .RuleFor(x => x.ExpiresAt, (Instant?)null)
                .RuleFor(x => x.ExpirySource, "uses")
                .RuleFor(x => x.QueryLimit, 5)
                .Generate());

        // When
        await RunJobAsync();

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var persisted = await readContext.Grants.FirstAsync(g => g.Id == grant.Id);
        persisted.Status.ShouldBe(GrantStatus.Active);
    }

    [Fact]
    public async Task When_LifetimeGrant_Then_NotExpiredByCron()
    {
        // Given — Lifetime grant: ExpiresAt null AND QueryLimit null (never expires)
        var (vaultId, orgId, agentId, userId) = await SetupAsync();
        var grant = await apiFactory.Services.SeedGranularGrantAsync(
            GrantFaker.CreateGranular(vaultId: vaultId, organizationId: orgId, agentId: agentId, createdBy: userId)
                .RuleFor(x => x.ExpiresAt, (Instant?)null)
                .RuleFor(x => x.ExpirySource, "lifetime")
                .RuleFor(x => x.QueryLimit, (int?)null)
                .Generate());

        // When
        await RunJobAsync();

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var persisted = await readContext.Grants.FirstAsync(g => g.Id == grant.Id);
        persisted.Status.ShouldBe(GrantStatus.Active);
    }

    [Theory]
    [InlineData(GrantStatus.Consumed)]
    [InlineData(GrantStatus.Revoked)]
    public async Task When_GrantNotActive_Then_LeftUnchangedEvenIfPastExpiry(GrantStatus status)
    {
        // Given — past ExpiresAt but a terminal status: must not be touched
        var (vaultId, orgId, agentId, userId) = await SetupAsync();
        var now = apiFactory.FakeClock.GetCurrentInstant();
        var grant = await apiFactory.Services.SeedGranularGrantAsync(
            GrantFaker.CreateGranular(vaultId: vaultId, organizationId: orgId, agentId: agentId,
                    createdBy: userId, status: status)
                .RuleFor(x => x.ExpiresAt, now.Minus(Duration.FromMinutes(5))).Generate());

        // When
        await RunJobAsync();

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var persisted = await readContext.Grants.FirstAsync(g => g.Id == grant.Id);
        persisted.Status.ShouldBe(status);
    }

    [Fact]
    public async Task When_JobRunsTwice_Then_Idempotent()
    {
        // Given
        var (vaultId, orgId, agentId, userId) = await SetupAsync();
        var now = apiFactory.FakeClock.GetCurrentInstant();
        var grant = await apiFactory.Services.SeedGranularGrantAsync(
            GrantFaker.CreateGranular(vaultId: vaultId, organizationId: orgId, agentId: agentId, createdBy: userId)
                .RuleFor(x => x.ExpiresAt, now.Minus(Duration.FromMinutes(5))).Generate());

        // When — two consecutive runs
        await RunJobAsync();
        await RunJobAsync();

        // Then — still exactly Expired, no error on the second pass
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var persisted = await readContext.Grants.FirstAsync(g => g.Id == grant.Id);
        persisted.Status.ShouldBe(GrantStatus.Expired);
    }
}
