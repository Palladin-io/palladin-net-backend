using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class OnOrganizationMemberRoleChangedAuditDefinition
    : ConsumerDefinition<OnOrganizationMemberRoleChangedAudit>
{
    public OnOrganizationMemberRoleChangedAuditDefinition() => EndpointName = IdentityEndpoints.Audit;
}

[UsedImplicitly]
internal sealed class OnOrganizationMemberRoleChangedAudit(IPublishEndpoint publishEndpoint)
    : IConsumer<OrganizationMemberRoleChangedEvent>
{
    public Task Consume(ConsumeContext<OrganizationMemberRoleChangedEvent> context)
    {
        var msg = context.Message;
        return publishEndpoint.Publish(OrganizationMembershipAuditCommand.Create(
            msg.OrganizationId, AuditEventType.OrganizationMemberRoleChanged, msg.OccurredAt,
            msg.ChangedBy, msg.ChangedByName,
            new Dictionary<string, string>
            {
                ["memberId"] = msg.UserId.ToString(),
                ["memberName"] = msg.UserDisplayName,
                ["oldRoles"] = string.Join(", ", msg.OldRoles),
                ["newRoles"] = string.Join(", ", msg.NewRoles),
            }), context.CancellationToken);
    }
}
