using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnGrantRequestedAuditDefinition : ConsumerDefinition<OnGrantRequestedAudit>
{
    public OnGrantRequestedAuditDefinition() => EndpointName = VaultEndpoints.Audit;
}

[UsedImplicitly]
internal sealed class OnGrantRequestedAudit(IPublishEndpoint publishEndpoint) : IConsumer<GrantRequestedEvent>
{
    public Task Consume(ConsumeContext<GrantRequestedEvent> context)
    {
        var msg = context.Message;
        return publishEndpoint.Publish(new AppendAuditLogCommand(
            OrganizationId: msg.OrganizationId,
            EventType: AuditEventType.GrantRequested,
            ActorType: AuditActorType.Agent, Result: AuditResult.Succeeded,
            OccurredAt: msg.UpdatedAt,
            UserId: null, AgentId: msg.AgentId, VaultId: msg.VaultId, EntryId: msg.EntryId,
            AgentName: msg.AgentName, ActorName: null, IpAddress: null,
            Metadata: new Dictionary<string, string>
            {
                ["grantId"] = msg.GrantId.ToString(),
                ["requestedMethods"] = msg.RequestedMethods.ToString(),
            }), context.CancellationToken);
    }
}
