using JetBrains.Annotations;
using MassTransit;
using Palladin.Module.Agents.Contracts.Events;
using Palladin.Module.Agents.Infrastructure.MassTransit;
using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;

namespace Palladin.Module.Agents.Triggers;

[UsedImplicitly]
internal sealed class OnAgentBrowserPairingEnrolledAuditDefinition
    : ConsumerDefinition<OnAgentBrowserPairingEnrolledAudit>
{
    public OnAgentBrowserPairingEnrolledAuditDefinition() => EndpointName = AgentsEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnAgentBrowserPairingEnrolledAudit(IPublishEndpoint publishEndpoint)
    : IConsumer<AgentBrowserPairingEnrolledEvent>
{
    public Task Consume(ConsumeContext<AgentBrowserPairingEnrolledEvent> context)
    {
        var message = context.Message;
        return publishEndpoint.Publish(new AppendAuditLogCommand(
            OrganizationId: message.OrganizationId,
            EventType: AuditEventType.AgentEnrolled,
            ActorType: AuditActorType.Agent,
            Result: AuditResult.Succeeded,
            OccurredAt: message.OccurredAt,
            UserId: null,
            AgentId: message.AgentId,
            VaultId: null,
            EntryId: null,
            AgentName: message.AgentName,
            ActorName: null,
            IpAddress: null,
            Metadata: new Dictionary<string, string> { ["status"] = "Active" }), context.CancellationToken);
    }
}
