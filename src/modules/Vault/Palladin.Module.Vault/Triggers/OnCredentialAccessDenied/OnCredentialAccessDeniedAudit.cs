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
internal sealed class OnCredentialAccessDeniedAuditDefinition : ConsumerDefinition<OnCredentialAccessDeniedAudit>
{
    public OnCredentialAccessDeniedAuditDefinition() => EndpointName = VaultEndpoints.Self;
}

// The event carries no org, so Vault resolves it from its own read-model before publishing.
[UsedImplicitly]
internal sealed class OnCredentialAccessDeniedAudit(
    VaultDomainReadContext readContext,
    IPublishEndpoint publishEndpoint) : IConsumer<CredentialAccessDeniedEvent>
{
    public async Task Consume(ConsumeContext<CredentialAccessDeniedEvent> context)
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
            EventType: AuditEventType.CredentialAccessDenied,
            ActorType: AuditActorType.Agent, Result: AuditResult.Denied,
            OccurredAt: msg.UpdatedAt,
            UserId: null, AgentId: msg.AgentId, VaultId: msg.VaultId, EntryId: msg.EntryId,
            AgentName: null, ActorName: null, IpAddress: null,
            Metadata: new Dictionary<string, string> { ["denialCode"] = msg.Reason }), ct);
    }
}
