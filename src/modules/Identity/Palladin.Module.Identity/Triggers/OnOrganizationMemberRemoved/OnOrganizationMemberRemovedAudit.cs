using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class OnOrganizationMemberRemovedAuditDefinition
    : ConsumerDefinition<OnOrganizationMemberRemovedAudit>
{
    public OnOrganizationMemberRemovedAuditDefinition() => EndpointName = IdentityEndpoints.Audit;
}

[UsedImplicitly]
internal sealed class OnOrganizationMemberRemovedAudit(IPublishEndpoint publishEndpoint)
    : IConsumer<OrganizationMemberRemovedEvent>
{
    public Task Consume(ConsumeContext<OrganizationMemberRemovedEvent> context)
    {
        var msg = context.Message;
        return publishEndpoint.Publish(OrganizationMembershipAuditCommand.Create(
            msg.OrganizationId, AuditEventType.OrganizationMemberRemoved, msg.OccurredAt,
            msg.RemovedBy, msg.RemovedByName,
            new Dictionary<string, string>
            {
                ["memberId"] = msg.UserId.ToString(),
                ["memberName"] = msg.UserDisplayName,
            }), context.CancellationToken);
    }
}
