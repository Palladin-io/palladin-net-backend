using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class OnOrganizationCreatedAuditDefinition : ConsumerDefinition<OnOrganizationCreatedAudit>
{
    public OnOrganizationCreatedAuditDefinition() => EndpointName = IdentityEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnOrganizationCreatedAudit(IPublishEndpoint publishEndpoint) : IConsumer<OrganizationCreatedEvent>
{
    public Task Consume(ConsumeContext<OrganizationCreatedEvent> context)
    {
        var msg = context.Message;
        return publishEndpoint.Publish(new AppendAuditLogCommand(
            OrganizationId: msg.OrganizationId,
            EventType: AuditEventType.OrganizationCreated,
            ActorType: AuditActorType.User, Result: AuditResult.Succeeded,
            OccurredAt: msg.CreatedAt,
            UserId: msg.CreatedBy,
            AgentId: null,
            VaultId: null,
            EntryId: null,
                        AgentName: null,
            ActorName: msg.CreatedByName,
                        IpAddress: null,
            Metadata: new Dictionary<string, string> { ["name"] = msg.Name }), context.CancellationToken);
    }
}
