using JetBrains.Annotations;
using MassTransit;
using Palladin.Module.Audit.Contracts.ValueObjects;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.MassTransit;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class OnOrganizationInvitationResentAuditDefinition
    : ConsumerDefinition<OnOrganizationInvitationResentAudit>
{
    public OnOrganizationInvitationResentAuditDefinition() => EndpointName = IdentityEndpoints.Audit;
}

[UsedImplicitly]
internal sealed class OnOrganizationInvitationResentAudit(IPublishEndpoint publishEndpoint)
    : IConsumer<OrganizationInvitationResentEvent>
{
    public Task Consume(ConsumeContext<OrganizationInvitationResentEvent> context)
    {
        var message = context.Message;
        return publishEndpoint.Publish(OrganizationMembershipAuditCommand.Create(
            message.OrganizationId,
            AuditEventType.OrganizationInvitationResent,
            message.OccurredAt,
            message.ResentBy,
            message.ResentByName,
            new Dictionary<string, string> { ["role"] = message.RoleName }),
            context.CancellationToken);
    }
}
