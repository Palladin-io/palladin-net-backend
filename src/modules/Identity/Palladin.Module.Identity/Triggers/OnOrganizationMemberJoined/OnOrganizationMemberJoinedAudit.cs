using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class OnOrganizationMemberJoinedAuditDefinition
    : ConsumerDefinition<OnOrganizationMemberJoinedAudit>
{
    public OnOrganizationMemberJoinedAuditDefinition() => EndpointName = IdentityEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnOrganizationMemberJoinedAudit(IPublishEndpoint publishEndpoint)
    : IConsumer<OrganizationMemberJoinedEvent>
{
    public Task Consume(ConsumeContext<OrganizationMemberJoinedEvent> context)
    {
        var msg = context.Message;
        return publishEndpoint.Publish(OrganizationMembershipAuditCommand.Create(
            msg.OrganizationId, AuditEventType.OrganizationMemberJoined, msg.OccurredAt,
            msg.UserId, msg.DisplayName,
            new Dictionary<string, string>
            {
                ["memberId"] = msg.UserId.ToString(),
                ["memberName"] = msg.DisplayName,
                ["role"] = msg.InitialRoleName,
            }), context.CancellationToken);
    }
}
