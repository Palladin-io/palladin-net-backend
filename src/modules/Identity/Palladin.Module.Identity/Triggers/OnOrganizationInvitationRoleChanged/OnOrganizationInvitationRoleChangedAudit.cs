using JetBrains.Annotations;
using MassTransit;
using Palladin.Module.Audit.Contracts.ValueObjects;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.MassTransit;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class OnOrganizationInvitationRoleChangedAuditDefinition
    : ConsumerDefinition<OnOrganizationInvitationRoleChangedAudit>
{
    public OnOrganizationInvitationRoleChangedAuditDefinition() => EndpointName = IdentityEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnOrganizationInvitationRoleChangedAudit(IPublishEndpoint publishEndpoint)
    : IConsumer<OrganizationInvitationRoleChangedEvent>
{
    public Task Consume(ConsumeContext<OrganizationInvitationRoleChangedEvent> context)
    {
        var message = context.Message;
        return publishEndpoint.Publish(OrganizationMembershipAuditCommand.Create(
            message.OrganizationId,
            AuditEventType.OrganizationInvitationRoleChanged,
            message.OccurredAt,
            message.ChangedBy,
            message.ChangedByName,
            new Dictionary<string, string>
            {
                ["previousRole"] = message.PreviousRoleName,
                ["role"] = message.RoleName,
            }),
            context.CancellationToken);
    }
}
