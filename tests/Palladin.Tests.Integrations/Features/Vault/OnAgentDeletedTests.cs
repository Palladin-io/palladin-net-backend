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
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

[Collection<ApiFactoryCollection>]
public sealed class OnAgentDeletedTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_AgentDeleted_Then_RemovesGrantsAndAgentReplica()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var agent = await apiFactory.Services.SeedVaultAgentAsync(organization.Id, AgentStatus.Deactivated);
        var grant = await apiFactory.Services.SeedGranularGrantAsync(
            GrantFaker.CreateGranular(
                organizationId: organization.Id,
                vaultId: vault.Id,
                agentId: agent.Id,
                status: GrantStatus.Revoked).Generate());

        // When
        await ConsumeAsync(new AgentDeletedEvent(
            agent.Id, organization.Id, user.Id, "Operator", "Agent", apiFactory.FakeClock.GetCurrentInstant()));

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.Grants.AnyAsync(g => g.Id == grant.Id)).ShouldBeFalse();
        (await readContext.Agents.AnyAsync(a => a.Id == agent.Id)).ShouldBeFalse();
    }

    [Fact]
    public async Task When_AgentReplicaAlreadyGone_Then_NoOp()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();

        // When
        await ConsumeAsync(new AgentDeletedEvent(
            Guid.NewGuid(), organization.Id, user.Id, "Operator", "Agent", apiFactory.FakeClock.GetCurrentInstant()));

        // Then — no throw; nothing to assert beyond graceful completion.
    }

    private async Task ConsumeAsync(AgentDeletedEvent @event)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var consumer = ActivatorUtilities.CreateInstance<OnAgentDeleted>(scope.ServiceProvider);
        await consumer.Consume(apiFactory.MockConsumeContext(@event));
    }
}
