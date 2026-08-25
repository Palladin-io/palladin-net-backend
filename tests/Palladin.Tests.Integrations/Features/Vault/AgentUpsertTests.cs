using Palladin.Core.Types;
using Palladin.Module.Agents.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Triggers;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Mocks;
using Palladin.Tests.Integrations.Shared.Seeders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

[Collection<ApiFactoryCollection>]
public sealed class AgentUpsertTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_AgentUpsertedAndReplicaMissing_Then_CreatesAgent()
    {
        // Given
        var agentId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();
        var now = TruncateToMs(apiFactory.FakeClock.GetCurrentInstant());

        // When
        await ConsumeUpsertedAsync(
            new AgentUpsertedEvent(agentId, organizationId, AgentStatus.Active, "pubkey", 1, "signing-pubkey", "Claude", "ci", "key:robot", "#FF4F4F", 1, now, now));

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var replica = await readContext.Agents.FirstOrDefaultAsync(a => a.Id == agentId);
        replica.ShouldNotBeNull();
        replica.OrganizationId.ShouldBe(organizationId);
        replica.Status.ShouldBe(AgentStatus.Active);
        replica.PublicKey.ShouldBe("pubkey");
        replica.IconKey.ShouldBe("key:robot");
        replica.IconColor.ShouldBe("#FF4F4F");
        replica.UpdatedAt.ShouldBe(now);
    }

    [Fact]
    public async Task When_AgentUpsertedWithNewerTimestamp_Then_UpdatesReplica()
    {
        // Given
        var agentId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();
        var t0 = TruncateToMs(apiFactory.FakeClock.GetCurrentInstant());
        await ConsumeUpsertedAsync(
            new AgentUpsertedEvent(agentId, organizationId, AgentStatus.Pending, "pubkey", 1, "signing-pubkey", "Claude", "ci", null, null, 0, null, t0));

        // When
        var t1 = t0 + Duration.FromSeconds(1);
        await ConsumeUpsertedAsync(
            new AgentUpsertedEvent(agentId, organizationId, AgentStatus.Active, "pubkey", 1, "signing-pubkey", "Claude", "ci", null, null, 1, t1, t1));

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var replica = await readContext.Agents.FirstAsync(a => a.Id == agentId);
        replica.Status.ShouldBe(AgentStatus.Active);
        replica.UpdatedAt.ShouldBe(t1);
    }

    [Theory]
    [InlineData(AgentStatus.Active, false, false)]
    [InlineData(AgentStatus.Pending, true, false)]
    [InlineData(AgentStatus.Active, true, true)]
    public async Task When_AgentLifecycleContractIsInvalid_Then_FailsClosed(
        AgentStatus status,
        bool includeAccessEpoch,
        bool epochStartsAfterUpdate)
    {
        // Given
        var updatedAt = TruncateToMs(apiFactory.FakeClock.GetCurrentInstant());
        var accessEpochStartedAt = includeAccessEpoch
            ? updatedAt + (epochStartsAfterUpdate ? Duration.FromSeconds(1) : Duration.Zero)
            : (Instant?)null;
        var message = new AgentUpsertedEvent(
            Guid.NewGuid(),
            Guid.NewGuid(),
            status,
            "pubkey",
            1,
            "signing-pubkey",
            "Agent",
            "ci",
            null,
            null,
            1,
            accessEpochStartedAt,
            updatedAt);

        // When / Then
        await Should.ThrowAsync<InvalidOperationException>(() => ConsumeUpsertedAsync(message));
    }

    [Fact]
    public async Task When_AgentUpsertedWithOlderTimestamp_Then_Ignored()
    {
        // Given
        var agentId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();
        var t1 = TruncateToMs(apiFactory.FakeClock.GetCurrentInstant()) + Duration.FromSeconds(10);
        await ConsumeUpsertedAsync(
            new AgentUpsertedEvent(agentId, organizationId, AgentStatus.Active, "pubkey", 1, "signing-pubkey", "Current", "ci", null, null, 1, t1, t1));

        // When
        var t0 = apiFactory.FakeClock.GetCurrentInstant();
        await ConsumeUpsertedAsync(
            new AgentUpsertedEvent(agentId, organizationId, AgentStatus.Deactivated, "pubkey", 1, "signing-pubkey", "Stale", "ci", null, null, 1, null, t0));

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var replica = await readContext.Agents.FirstAsync(a => a.Id == agentId);
        replica.Status.ShouldBe(AgentStatus.Active);
    }

    [Fact]
    public async Task When_GrantSubstitutesAgentFromAnotherOrganization_Then_CompositeForeignKeyRejectsIt()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var (_, foreignOrganization, _) = await apiFactory.Services.SeedUserAsync();
        var foreignAgent = await apiFactory.Services.SeedVaultAgentAsync(foreignOrganization.Id);
        var grant = GrantFaker.CreateFull(
            vaultId: vault.Id,
            organizationId: organization.Id,
            agentId: foreignAgent.Id,
            createdBy: user.Id).Generate();

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
        writeContext.Grants.Add(grant);

        // When / Then
        await Should.ThrowAsync<DbUpdateException>(() => writeContext.SaveChangesAsync());
    }

    [Fact]
    public async Task When_StaleActiveUpsertArrivesAfterDeactivation_Then_DoesNotReactivateReplica()
    {
        // Given
        var agentId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();
        var t0 = TruncateToMs(apiFactory.FakeClock.GetCurrentInstant());
        await ConsumeUpsertedAsync(
            new AgentUpsertedEvent(agentId, organizationId, AgentStatus.Active, "pubkey", 1, "signing-pubkey", "Claude", "ci", null, null, 1, t0, t0));

        var t2 = t0 + Duration.FromSeconds(2);
        await ConsumeDeactivatedAsync(
            new AgentDeactivatedEvent(agentId, organizationId, Guid.NewGuid(), "Operator", "Agent", 1, t2));

        // When
        var t1 = t0 + Duration.FromSeconds(1);
        await ConsumeUpsertedAsync(
            new AgentUpsertedEvent(agentId, organizationId, AgentStatus.Active, "pubkey", 1, "signing-pubkey", "Claude", "ci", null, null, 1, t0, t1));

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var replica = await readContext.Agents.FirstAsync(a => a.Id == agentId);
        replica.Status.ShouldBe(AgentStatus.Deactivated);
        replica.UpdatedAt.ShouldBe(t2);
    }

    [Fact]
    public async Task When_DeactivationArrivesAfterReactivation_Then_CascadesOnceWithoutRollingBackReplica()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var t0 = TruncateToMs(apiFactory.FakeClock.GetCurrentInstant());
        var agent = await apiFactory.Services.SeedVaultAgentAsync(organization.Id, updatedAt: t0);
        var oldGrant = await apiFactory.Services.SeedGranularGrantAsync(
            GrantFaker.CreateGranular(
                vaultId: vault.Id,
                organizationId: organization.Id,
                agentId: agent.Id,
                agentAccessEpoch: 1,
                createdBy: user.Id,
                status: GrantStatus.Active,
                createdAt: t0).Generate());
        var deactivatedAt = t0 + Duration.FromSeconds(1);
        var reactivatedAt = t0 + Duration.FromSeconds(2);
        await ConsumeUpsertedAsync(new AgentUpsertedEvent(
            agent.Id,
            organization.Id,
            AgentStatus.Active,
            agent.PublicKey,
            agent.RecipientKeyVersion,
            agent.SigningPublicKey,
            agent.Name,
            "ci",
            agent.IconKey,
            agent.IconColor,
            2,
            reactivatedAt,
            reactivatedAt));
        var metadataUpdatedAt = reactivatedAt + Duration.FromSeconds(1);
        await ConsumeUpsertedAsync(new AgentUpsertedEvent(
            agent.Id,
            organization.Id,
            AgentStatus.Active,
            agent.PublicKey,
            agent.RecipientKeyVersion,
            agent.SigningPublicKey,
            "Renamed after reactivation",
            "ci",
            agent.IconKey,
            agent.IconColor,
            2,
            reactivatedAt,
            metadataUpdatedAt));
        var postReactivationGrant = await apiFactory.Services.SeedGranularGrantAsync(
            GrantFaker.CreateGranular(
                vaultId: vault.Id,
                organizationId: organization.Id,
                agentId: agent.Id,
                agentAccessEpoch: 2,
                createdBy: user.Id,
                status: GrantStatus.Active,
                createdAt: reactivatedAt).Generate());

        // When
        var deactivation = new AgentDeactivatedEvent(
            agent.Id,
            organization.Id,
            user.Id,
            "Operator",
            "Agent",
            1,
            deactivatedAt);
        await ConsumeDeactivatedAsync(deactivation);

        // Then
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
            var replica = await readContext.Agents.FirstAsync(a => a.Id == agent.Id);
            replica.Status.ShouldBe(AgentStatus.Active);
            replica.UpdatedAt.ShouldBe(metadataUpdatedAt);
            replica.AccessEpochStartedAt.ShouldBe(reactivatedAt);
            replica.LastProcessedDeactivationAt.ShouldBe(deactivatedAt);
            (await readContext.Grants.FirstAsync(g => g.Id == oldGrant.Id)).Status
                .ShouldBe(GrantStatus.Revoked);
            (await readContext.Grants.FirstAsync(g => g.Id == postReactivationGrant.Id)).Status
                .ShouldBe(GrantStatus.Active);
        }

        await ConsumeDeactivatedAsync(deactivation);

        await using var retryScope = apiFactory.Services.CreateAsyncScope();
        var retryReadContext = retryScope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await retryReadContext.Grants.FirstAsync(g => g.Id == postReactivationGrant.Id)).Status
            .ShouldBe(GrantStatus.Active);
    }

    [Fact]
    public async Task When_SubMicrosecondDeactivationIsRedelivered_Then_UsesCanonicalWatermark()
    {
        // Given
        var organizationId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var t0 = TruncateToMs(apiFactory.FakeClock.GetCurrentInstant());
        await ConsumeUpsertedAsync(new AgentUpsertedEvent(
            agentId,
            organizationId,
            AgentStatus.Active,
            "pubkey",
            1,
            "signing-pubkey",
            "Agent",
            "ci",
            null,
            null,
            1,
            t0,
            t0));
        var rawDeactivatedAt = t0 + Duration.FromSeconds(1) + Duration.FromTicks(7);
        var canonicalDeactivatedAt = Instant.FromUnixTimeTicks(rawDeactivatedAt.ToUnixTimeTicks() - 7);
        var deactivation = new AgentDeactivatedEvent(
            agentId,
            organizationId,
            Guid.NewGuid(),
            "Operator",
            "Agent",
            1,
            rawDeactivatedAt);

        // When
        await ConsumeDeactivatedAsync(deactivation);
        await ConsumeDeactivatedAsync(deactivation);

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var replica = await readContext.Agents.SingleAsync(a => a.Id == agentId);
        replica.Status.ShouldBe(AgentStatus.Deactivated);
        replica.UpdatedAt.ShouldBe(canonicalDeactivatedAt);
        replica.LastProcessedDeactivationAt.ShouldBe(canonicalDeactivatedAt);
        replica.AccessEpochStartedAt.ShouldBeNull();
    }

    [Fact]
    public async Task When_AgentDeactivated_Then_CascadeRevokesActiveGrantsAndDeletesAgentMaterial()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var agent = await apiFactory.Services.SeedVaultAgentAsync(organization.Id);

        var activeGrant = await apiFactory.Services.SeedGranularGrantAsync(
            GrantFaker.CreateGranular(vaultId: vault.Id, organizationId: organization.Id, agentId: agent.Id,
                createdBy: user.Id, status: GrantStatus.Active).Generate());
        var activeFullGrant = await apiFactory.Services.SeedFullGrantAsync(
            GrantFaker.CreateFull(vaultId: vault.Id, organizationId: organization.Id, agentId: agent.Id,
                createdBy: user.Id, status: GrantStatus.Active).Generate());
        var alreadyRevoked = await apiFactory.Services.SeedGranularGrantAsync(
            GrantFaker.CreateGranular(vaultId: vault.Id, organizationId: organization.Id, agentId: agent.Id,
                createdBy: user.Id, status: GrantStatus.Revoked).Generate());

        // When
        await ConsumeDeactivatedAsync(
            new AgentDeactivatedEvent(
                agent.Id,
                organization.Id,
                user.Id,
                "Operator",
                "Agent",
                1,
                agent.UpdatedAt + Duration.FromMilliseconds(1)));

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();

        var revoked = await readContext.Grants.FirstAsync(g => g.Id == activeGrant.Id);
        revoked.Status.ShouldBe(GrantStatus.Revoked);
        revoked.RevokedBySystem.ShouldBeTrue();
        revoked.RevokedBy.ShouldBeNull();

        var revokedFull = await readContext.Grants.FirstAsync(g => g.Id == activeFullGrant.Id);
        revokedFull.Status.ShouldBe(GrantStatus.Revoked);
        (await readContext.AgentWrappedVaultKeys.CountAsync(x => x.GrantId == activeFullGrant.Id))
            .ShouldBe(0);

        var untouched = await readContext.Grants.FirstAsync(g => g.Id == alreadyRevoked.Id);
        untouched.RevokedBySystem.ShouldBeFalse();

        await apiFactory.Services.SeedFullGrantAsync(
            GrantFaker.CreateFull(vaultId: vault.Id, organizationId: organization.Id, agentId: agent.Id,
                createdBy: user.Id, status: GrantStatus.Active).Generate());
    }

    [Fact]
    public async Task When_AgentHasMoreThanOneGrantPage_Then_DeactivationRevokesEveryPage()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var agent = await apiFactory.Services.SeedVaultAgentAsync(organization.Id);
        await using (var seedScope = apiFactory.Services.CreateAsyncScope())
        {
            var writeContext = seedScope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
            writeContext.Grants.AddRange(Enumerable.Range(0, OnAgentDeactivated.PageSize + 1)
                .Select(_ => GrantFaker.CreateGranular(
                    vaultId: vault.Id,
                    organizationId: organization.Id,
                    agentId: agent.Id,
                    createdBy: user.Id,
                    status: GrantStatus.Active).Generate()));
            await writeContext.SaveChangesAsync();
        }

        // When
        await ConsumeDeactivatedAsync(new AgentDeactivatedEvent(
            agent.Id,
            organization.Id,
            user.Id,
            "Operator",
            "Agent",
            1,
            agent.UpdatedAt + Duration.FromMilliseconds(1)));

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.Grants.CountAsync(x =>
            x.OrganizationId == organization.Id
            && x.AgentId == agent.Id
            && x.Status == GrantStatus.Revoked)).ShouldBe(OnAgentDeactivated.PageSize + 1);
    }

    [Fact]
    public async Task When_DeactivationRetryResumesAfterReplicaWasPersisted_Then_UnfinishedGrantIsRevoked()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var agent = await apiFactory.Services.SeedVaultAgentAsync(organization.Id);
        var deactivation = new AgentDeactivatedEvent(
            agent.Id,
            organization.Id,
            user.Id,
            "Operator",
            "Agent",
            1,
            agent.UpdatedAt + Duration.FromMilliseconds(1));
        await ConsumeDeactivatedAsync(deactivation);
        var unfinishedGrant = await apiFactory.Services.SeedGranularGrantAsync(
            GrantFaker.CreateGranular(
                vaultId: vault.Id,
                organizationId: organization.Id,
                agentId: agent.Id,
                createdBy: user.Id,
                status: GrantStatus.Active).Generate());

        // When
        await ConsumeDeactivatedAsync(deactivation);

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var persisted = await scope.ServiceProvider.GetRequiredService<VaultDbReadContext>()
            .Grants.SingleAsync(x => x.Id == unfinishedGrant.Id);
        persisted.Status.ShouldBe(GrantStatus.Revoked);
    }

    private async Task ConsumeUpsertedAsync(AgentUpsertedEvent @event)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var consumer = new OnAgentUpserted(
            scope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>());

        await consumer.Consume(apiFactory.MockConsumeContext(@event));
    }

    private async Task ConsumeDeactivatedAsync(AgentDeactivatedEvent @event)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var consumer = new OnAgentDeactivated(
            scope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>(),
            scope.ServiceProvider.GetRequiredService<IClock>());

        await consumer.Consume(apiFactory.MockConsumeContext(@event));
    }

    private static Instant TruncateToMs(Instant instant) =>
        Instant.FromUnixTimeMilliseconds(instant.ToUnixTimeMilliseconds());
}
