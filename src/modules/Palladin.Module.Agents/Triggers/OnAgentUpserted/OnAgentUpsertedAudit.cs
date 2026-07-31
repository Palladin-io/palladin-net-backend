using Palladin.Core.Types;
using Palladin.Module.Agents.Contracts.Events;
using Palladin.Module.Agents.Infrastructure.MassTransit;
using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Agents.Triggers;

[UsedImplicitly]
internal sealed class OnAgentUpsertedAuditDefinition : ConsumerDefinition<OnAgentUpsertedAudit>
{
    public OnAgentUpsertedAuditDefinition() => EndpointName = AgentsEndpoints.Audit;
}

// Records agent.enrolled when an agent first appears in pending status. Other upsert transitions
// (active/profile/key changes) are not audited here to avoid noise.
[UsedImplicitly]
internal sealed class OnAgentUpsertedAudit(IPublishEndpoint publishEndpoint) : IConsumer<AgentUpsertedEvent>
{
    public Task Consume(ConsumeContext<AgentUpsertedEvent> context)
    {
        var msg = context.Message;
        if (msg.Status != AgentStatus.Pending)
        {
            return Task.CompletedTask;
        }

        return publishEndpoint.Publish(new AppendAuditLogCommand(
            OrganizationId: msg.OrganizationId,
            EventType: AuditEventType.AgentEnrolled,
            ActorType: AuditActorType.Agent, Result: AuditResult.Succeeded,
            OccurredAt: msg.UpdatedAt,
            UserId: null, AgentId: msg.AgentId, VaultId: null, EntryId: null, AgentName: msg.Name, ActorName: null, IpAddress: null,
            Metadata: new Dictionary<string, string> { ["status"] = msg.Status.ToString() }), context.CancellationToken);
    }
}
