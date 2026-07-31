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
internal sealed class OnEntryDeletedAuditDefinition : ConsumerDefinition<OnEntryDeletedAudit>
{
    public OnEntryDeletedAuditDefinition() => EndpointName = VaultEndpoints.Audit;
}

[UsedImplicitly]
internal sealed class OnEntryDeletedAudit(
    VaultDomainReadContext readContext,
    IPublishEndpoint publishEndpoint) : IConsumer<EntryDeletedEvent>
{
    public async Task Consume(ConsumeContext<EntryDeletedEvent> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        var organizationId = await readContext.Vaults
            .Where(v => v.Id == msg.VaultId)
            .Select(v => (Guid?)v.OrganizationId)
            .FirstOrDefaultAsync(ct);
        if (organizationId is null)
        {
            return;
        }

        await publishEndpoint.Publish(new AppendAuditLogCommand(
            OrganizationId: organizationId.Value,
            EventType: AuditEventType.EntryDeleted,
            ActorType: AuditActorType.User, Result: AuditResult.Succeeded,
            OccurredAt: msg.DeletedAt,
            UserId: msg.DeletedBy, AgentId: null, VaultId: msg.VaultId, EntryId: msg.EntryId,
            AgentName: null, ActorName: msg.ActorName, IpAddress: null,
            Metadata: new Dictionary<string, string>()), ct);
    }
}
