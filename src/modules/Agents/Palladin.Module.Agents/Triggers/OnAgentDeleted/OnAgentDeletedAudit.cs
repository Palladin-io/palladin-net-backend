using Palladin.Module.Agents.Contracts.Events;
using Palladin.Module.Agents.Infrastructure.MassTransit;
using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Agents.Triggers;

[UsedImplicitly]
internal sealed class OnAgentDeletedAuditDefinition : ConsumerDefinition<OnAgentDeletedAudit>
{
    public OnAgentDeletedAuditDefinition() => EndpointName = AgentsEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnAgentDeletedAudit(IPublishEndpoint publishEndpoint) : IConsumer<AgentDeletedEvent>
{
    public Task Consume(ConsumeContext<AgentDeletedEvent> context)
    {
        var msg = context.Message;
        return publishEndpoint.Publish(new AppendAuditLogCommand(
            OrganizationId: msg.OrganizationId,
            EventType: AuditEventType.AgentDeleted,
            ActorType: AuditActorType.User, Result: AuditResult.Succeeded,
            OccurredAt: msg.DeletedAt,
            UserId: msg.DeletedBy, AgentId: msg.AgentId, VaultId: null, EntryId: null, AgentName: msg.AgentName, ActorName: msg.DeletedByName, IpAddress: null,
            Metadata: new Dictionary<string, string>()), context.CancellationToken);
    }
}
