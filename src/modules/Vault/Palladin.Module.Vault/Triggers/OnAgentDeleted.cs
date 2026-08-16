using Palladin.Module.Agents.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.MassTransit;
using Palladin.Module.Vault.Infrastructure.Persistence;
using JetBrains.Annotations;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnAgentDeletedDefinition : ConsumerDefinition<OnAgentDeleted>
{
    public OnAgentDeletedDefinition() => EndpointName = VaultEndpoints.FromAgents;
}

// Cascade removal: hard deletion removes grants, the discovery revision tombstone and the read-model
// replica. Grants and discovery carry Restrict FKs, so both dependents must go first.
[UsedImplicitly]
internal sealed class OnAgentDeleted(
    VaultDomainWriteContext domainWriteContext) : IConsumer<AgentDeletedEvent>
{
    public async Task Consume(ConsumeContext<AgentDeletedEvent> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        var agent = await domainWriteContext.Agents.SingleOrDefaultAsync(
            x => x.OrganizationId == msg.OrganizationId && x.Id == msg.AgentId,
            ct);

        if (agent is null)
        {
            return;
        }

        var grants = await domainWriteContext.Grants
            .Where(g => g.AgentId == msg.AgentId)
            .ToListAsync(ct);
        var vaults = await domainWriteContext.Vaults
            .Include(x => x.AgentVaultDiscoveryEnvelopes)
            .Where(x => x.OrganizationId == msg.OrganizationId)
            .Where(x => x.AgentVaultDiscoveryEnvelopes.Any(envelope => envelope.AgentId == msg.AgentId))
            .ToListAsync(ct);

        foreach (var vault in vaults)
        {
            vault.DeleteAgentDiscovery(msg.AgentId);
        }

        domainWriteContext.RemoveRange(grants);
        domainWriteContext.Remove(agent);
        await domainWriteContext.CommitAsync(ct);
    }
}
