using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.MassTransit;
using Palladin.Module.Vault.Infrastructure.Persistence;
using JetBrains.Annotations;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnVaultExportedAuditDefinition : ConsumerDefinition<OnVaultExportedAudit>
{
    public OnVaultExportedAuditDefinition() => EndpointName = VaultEndpoints.Audit;
}

// The export event carries no org, so Vault resolves the tenant from its own read-model before
// publishing the audit append command.
[UsedImplicitly]
internal sealed class OnVaultExportedAudit(
    VaultDomainReadContext readContext,
    IPublishEndpoint publishEndpoint) : IConsumer<VaultExportedEvent>
{
    public async Task Consume(ConsumeContext<VaultExportedEvent> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        var vault = await readContext.Vaults
            .Where(v => v.Id == msg.VaultId)
            .Select(v => new { v.OrganizationId })
            .FirstOrDefaultAsync(ct);
        if (vault is null)
        {
            return;
        }

        await publishEndpoint.Publish(
            new AppendAuditLogCommand(
                OrganizationId: vault.OrganizationId,
                EventType: AuditEventType.VaultExported,
                ActorType: AuditActorType.User, Result: AuditResult.Succeeded,
                OccurredAt: msg.UpdatedAt,
                UserId: msg.UserId, AgentId: null, VaultId: msg.VaultId, EntryId: null,
                AgentName: null, ActorName: msg.ActorName, IpAddress: null,
                Metadata: new Dictionary<string, string>
                {
                    ["format"] = msg.Format,
                    ["entryCount"] = msg.EntryCount.ToString(),
                }),
            ct);
    }
}
