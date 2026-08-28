using Palladin.Core.Types;
using Palladin.Module.Agents.Contracts.Events;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.MassTransit;
using Palladin.Module.Vault.Infrastructure.Persistence;
using JetBrains.Annotations;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnAgentDeactivatedDefinition : ConsumerDefinition<OnAgentDeactivated>
{
    public OnAgentDeactivatedDefinition() => EndpointName = VaultEndpoints.FromAgents;
}

// Cascade revoke: when an agent is deactivated, all its active grants are revoked by the system.
// This closes the replica-staleness window without a synchronous live check on the delivery path.
[UsedImplicitly]
internal sealed class OnAgentDeactivated(
    VaultDomainWriteContext domainWriteContext,
    IClock clock) : IConsumer<AgentDeactivatedEvent>
{
    internal const int PageSize = 100;

    public async Task Consume(ConsumeContext<AgentDeactivatedEvent> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;
        var now = clock.GetCurrentInstant();
        var deactivatedAt = PostgreSqlInstant.Normalize(msg.UpdatedAt);
        string agentName;
        var agent = await domainWriteContext.Agents.SingleOrDefaultAsync(
            x => x.OrganizationId == msg.OrganizationId && x.Id == msg.AgentId,
            ct);
        if (agent is null)
        {
            return;
        }

        var accepted = agent.AcceptDeactivation(msg.AccessEpoch, deactivatedAt);
        if (!accepted
            && (agent.LastProcessedDeactivationEpoch != msg.AccessEpoch
                || agent.LastProcessedDeactivationAt != deactivatedAt))
        {
            return;
        }

        agentName = agent.Name ?? GrantNames.UnknownAgent;
        await domainWriteContext.CommitAsync(ct);
        domainWriteContext.Clear();

        Guid? lastGrantId = null;
        while (true)
        {
            var query = domainWriteContext.Grants
                .Include(g => g.EncryptedReason)
                .Include(g => g.AgentWrappedVaultKey)
                .Include(g => g.ScriptExecutionPackage)
                .Include(g => g.GrantEntryScopes).ThenInclude(scope => scope.Envelope)
                .Where(g => g.OrganizationId == msg.OrganizationId && g.AgentId == msg.AgentId)
                .Where(g => g.AgentAccessEpoch <= msg.AccessEpoch)
                .Where(g => g.Status == GrantStatus.Active || g.Status == GrantStatus.Pending);
            if (lastGrantId is not null)
            {
                query = query.Where(x => x.Id.CompareTo(lastGrantId.Value) > 0);
            }

            var grants = await query.OrderBy(x => x.Id).Take(PageSize).ToListAsync(ct);
            if (grants.Count == 0)
            {
                break;
            }

            foreach (var grant in grants)
            {
                grant.DeleteAgentEnvelopes();
                grant.RevokeBySystem(
                    new GrantNames(agentName, null, string.Empty, GrantNames.SystemActor), now);
            }

            lastGrantId = grants[^1].Id;
            await domainWriteContext.CommitAsync(ct);
            domainWriteContext.Clear();
            if (grants.Count < PageSize)
            {
                break;
            }
        }

        Guid? lastVaultId = null;
        while (true)
        {
            var query = domainWriteContext.Vaults
                .Include(x => x.AgentVaultDiscoveryEnvelopes.Where(envelope => envelope.AgentId == msg.AgentId))
                .Where(x => x.OrganizationId == msg.OrganizationId)
                .Where(x => x.AgentVaultDiscoveryEnvelopes.Any(envelope => envelope.AgentId == msg.AgentId));
            if (lastVaultId is not null)
            {
                query = query.Where(x => x.Id.CompareTo(lastVaultId.Value) > 0);
            }

            var vaults = await query.OrderBy(x => x.Id).Take(PageSize).ToListAsync(ct);
            if (vaults.Count == 0)
            {
                break;
            }

            foreach (var vault in vaults)
            {
                vault.RevokeAgentDiscovery(msg.AgentId, deactivatedAt, msg.AccessEpoch);
            }

            lastVaultId = vaults[^1].Id;
            await domainWriteContext.CommitAsync(ct);
            domainWriteContext.Clear();
            if (vaults.Count < PageSize)
            {
                break;
            }
        }
    }
}
