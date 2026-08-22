using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class OnUserSignedUpAuditDefinition : ConsumerDefinition<OnUserSignedUpAudit>
{
    public OnUserSignedUpAuditDefinition() => EndpointName = IdentityEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnUserSignedUpAudit(IPublishEndpoint publishEndpoint) : IConsumer<UserSignedUpEvent>
{
    public Task Consume(ConsumeContext<UserSignedUpEvent> context)
    {
        var msg = context.Message;
        return publishEndpoint.Publish(new AppendAuditLogCommand(
            OrganizationId: msg.OrganizationId,
            EventType: AuditEventType.UserSignedUp,
            ActorType: AuditActorType.User, Result: AuditResult.Succeeded,
            OccurredAt: msg.CreatedAt,
            UserId: msg.UserId,
            AgentId: null,
            VaultId: null,
            EntryId: null,
                        AgentName: null,
            ActorName: msg.DisplayName,
                        IpAddress: null,
            Metadata: new Dictionary<string, string>
            {
                ["provider"] = msg.Provider,
                ["platform"] = msg.Platform,
            }), context.CancellationToken);
    }
}
