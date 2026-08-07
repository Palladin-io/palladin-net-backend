using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class OnOrganizationUpdatedAuditDefinition : ConsumerDefinition<OnOrganizationUpdatedAudit>
{
    public OnOrganizationUpdatedAuditDefinition() => EndpointName = IdentityEndpoints.Audit;
}

[UsedImplicitly]
internal sealed class OnOrganizationUpdatedAudit(IPublishEndpoint publishEndpoint) : IConsumer<OrganizationUpdatedEvent>
{
    public Task Consume(ConsumeContext<OrganizationUpdatedEvent> context)
    {
        var msg = context.Message;
        return publishEndpoint.Publish(new AppendAuditLogCommand(
            OrganizationId: msg.OrganizationId,
            EventType: AuditEventType.OrganizationUpdated,
            ActorType: AuditActorType.User, Result: AuditResult.Succeeded,
            OccurredAt: msg.UpdatedAt,
            UserId: msg.UpdatedBy,
            AgentId: null,
            VaultId: null,
            EntryId: null,
                        AgentName: null,
            ActorName: msg.UpdatedByName,
                        IpAddress: null,
            Metadata: new Dictionary<string, string>
            {
                ["name"] = msg.Name,
                ["fieldsChanged"] = string.Join(',', msg.FieldsChanged),
            }), context.CancellationToken);
    }
}
