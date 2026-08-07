using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnGrantConsumedAuditDefinition : ConsumerDefinition<OnGrantConsumedAudit>
{
    public OnGrantConsumedAuditDefinition() => EndpointName = VaultEndpoints.Audit;
}

[UsedImplicitly]
internal sealed class OnGrantConsumedAudit(IPublishEndpoint publishEndpoint) : IConsumer<GrantConsumedEvent>
{
    public Task Consume(ConsumeContext<GrantConsumedEvent> context)
    {
        var msg = context.Message;
        return publishEndpoint.Publish(new AppendAuditLogCommand(
            OrganizationId: msg.OrganizationId,
            EventType: AuditEventType.GrantConsumed,
            ActorType: AuditActorType.System, Result: AuditResult.Succeeded,
            OccurredAt: msg.UpdatedAt,
            UserId: null, AgentId: msg.AgentId, VaultId: msg.VaultId, EntryId: msg.EntryId,
            AgentName: null, ActorName: null, IpAddress: null,
            Metadata: new Dictionary<string, string> { ["grantId"] = msg.GrantId.ToString() }),
            context.CancellationToken);
    }
}
