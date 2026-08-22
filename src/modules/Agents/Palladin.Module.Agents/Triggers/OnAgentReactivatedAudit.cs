using Palladin.Module.Agents.Contracts.Events;
using Palladin.Module.Agents.Infrastructure.MassTransit;
using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Agents.Triggers;

[UsedImplicitly]
internal sealed class OnAgentReactivatedAuditDefinition : ConsumerDefinition<OnAgentReactivatedAudit>
{
    public OnAgentReactivatedAuditDefinition() => EndpointName = AgentsEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnAgentReactivatedAudit(IPublishEndpoint publishEndpoint) : IConsumer<AgentReactivatedEvent>
{
    public Task Consume(ConsumeContext<AgentReactivatedEvent> context)
    {
        var msg = context.Message;
        return publishEndpoint.Publish(new AppendAuditLogCommand(
            OrganizationId: msg.OrganizationId,
            EventType: AuditEventType.AgentReactivated,
            ActorType: AuditActorType.User, Result: AuditResult.Succeeded,
            OccurredAt: msg.UpdatedAt,
            UserId: msg.ReactivatedBy, AgentId: msg.AgentId, VaultId: null, EntryId: null, AgentName: msg.AgentName, ActorName: msg.ReactivatedByName, IpAddress: null,
            Metadata: new Dictionary<string, string>()), context.CancellationToken);
    }
}
