using JetBrains.Annotations;
using MassTransit;
using Palladin.Module.Audit.Contracts.ValueObjects;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.MassTransit;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class OnOrganizationInvitationCancelledAuditDefinition
    : ConsumerDefinition<OnOrganizationInvitationCancelledAudit>
{
    public OnOrganizationInvitationCancelledAuditDefinition() => EndpointName = IdentityEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnOrganizationInvitationCancelledAudit(IPublishEndpoint publishEndpoint)
    : IConsumer<OrganizationInvitationCancelledEvent>
{
    public Task Consume(ConsumeContext<OrganizationInvitationCancelledEvent> context)
    {
        var message = context.Message;
        return publishEndpoint.Publish(OrganizationMembershipAuditCommand.Create(
            message.OrganizationId,
            AuditEventType.OrganizationInvitationCancelled,
            message.OccurredAt,
            message.CancelledBy,
            message.CancelledByName,
            new Dictionary<string, string> { ["role"] = message.RoleName }),
            context.CancellationToken);
    }
}
