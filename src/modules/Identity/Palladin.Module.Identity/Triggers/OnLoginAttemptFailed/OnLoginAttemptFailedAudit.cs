using JetBrains.Annotations;
using MassTransit;
using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.MassTransit;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class OnLoginAttemptFailedAuditDefinition
    : ConsumerDefinition<OnLoginAttemptFailedAudit>
{
    public OnLoginAttemptFailedAuditDefinition() => EndpointName = IdentityEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnLoginAttemptFailedAudit(IPublishEndpoint publishEndpoint)
    : IConsumer<LoginAttemptFailedEvent>
{
    public Task Consume(ConsumeContext<LoginAttemptFailedEvent> context)
    {
        var msg = context.Message;
        if (msg.OrganizationId is not { } organizationId
            || msg.TargetUserId is not { } targetUserId)
        {
            return Task.CompletedTask;
        }

        return publishEndpoint.Publish(new AppendAuditLogCommand(
            OrganizationId: organizationId,
            EventType: AuditEventType.LoginFailed,
            ActorType: AuditActorType.System,
            Result: AuditResult.Denied,
            OccurredAt: msg.OccurredAt,
            UserId: null,
            AgentId: null,
            VaultId: null,
            EntryId: null,
            AgentName: null,
            ActorName: null,
            IpAddress: msg.IpAddress,
            Metadata: new Dictionary<string, string>
            {
                ["factor"] = msg.Factor,
                ["targetUserId"] = targetUserId.ToString(),
            },
            IdempotencyKey: msg.AttemptId), context.CancellationToken);
    }
}
