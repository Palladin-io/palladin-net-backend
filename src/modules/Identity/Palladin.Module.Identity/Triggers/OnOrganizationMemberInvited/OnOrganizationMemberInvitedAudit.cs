using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class OnOrganizationMemberInvitedAuditDefinition
    : ConsumerDefinition<OnOrganizationMemberInvitedAudit>
{
    public OnOrganizationMemberInvitedAuditDefinition() => EndpointName = IdentityEndpoints.Audit;
}

[UsedImplicitly]
internal sealed class OnOrganizationMemberInvitedAudit(IPublishEndpoint publishEndpoint)
    : IConsumer<OrganizationMemberInvitedEvent>
{
    public Task Consume(ConsumeContext<OrganizationMemberInvitedEvent> context)
    {
        var msg = context.Message;
        return publishEndpoint.Publish(OrganizationMembershipAuditCommand.Create(
            msg.OrganizationId, AuditEventType.OrganizationMemberInvited, msg.OccurredAt,
            msg.InvitedBy, msg.InvitedByName,
            new Dictionary<string, string> { ["role"] = msg.RoleName }), context.CancellationToken);
    }
}
