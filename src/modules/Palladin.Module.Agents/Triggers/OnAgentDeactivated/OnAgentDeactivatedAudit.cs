using Palladin.Module.Agents.Contracts.Events;
using Palladin.Module.Agents.Infrastructure.MassTransit;
using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Agents.Triggers;

[UsedImplicitly]
internal sealed class OnAgentDeactivatedAuditDefinition : ConsumerDefinition<OnAgentDeactivatedAudit>
{
    public OnAgentDeactivatedAuditDefinition() => EndpointName = AgentsEndpoints.Audit;
}

[UsedImplicitly]
internal sealed class OnAgentDeactivatedAudit(IPublishEndpoint publishEndpoint) : IConsumer<AgentDeactivatedEvent>
{
    public Task Consume(ConsumeContext<AgentDeactivatedEvent> context)
    {
        var msg = context.Message;
        return publishEndpoint.Publish(new AppendAuditLogCommand(
            OrganizationId: msg.OrganizationId,
            EventType: AuditEventType.AgentBlocked,
            ActorType: AuditActorType.User, Result: AuditResult.Succeeded,
            OccurredAt: msg.UpdatedAt,
            UserId: msg.DeactivatedBy, AgentId: msg.AgentId, VaultId: null, EntryId: null, AgentName: msg.AgentName, ActorName: msg.DeactivatedByName, IpAddress: null,
            Metadata: new Dictionary<string, string>()), context.CancellationToken);
    }
}
