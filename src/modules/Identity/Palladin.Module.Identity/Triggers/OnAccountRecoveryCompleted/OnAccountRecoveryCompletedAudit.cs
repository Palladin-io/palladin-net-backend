using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Identity.Triggers;

// Security-critical: account recovery rotates the user's key material. We record ONLY that recovery
// happened and who — never the recovery material, salts, keys or any secret.
[UsedImplicitly]
internal sealed class OnAccountRecoveryCompletedAuditDefinition : ConsumerDefinition<OnAccountRecoveryCompletedAudit>
{
    public OnAccountRecoveryCompletedAuditDefinition() => EndpointName = IdentityEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnAccountRecoveryCompletedAudit(IPublishEndpoint publishEndpoint) : IConsumer<AccountRecoveryCompletedEvent>
{
    public Task Consume(ConsumeContext<AccountRecoveryCompletedEvent> context)
    {
        var msg = context.Message;
        return publishEndpoint.Publish(new AppendAuditLogCommand(
            OrganizationId: msg.OrganizationId,
            EventType: AuditEventType.AccountRecoveryCompleted,
            ActorType: AuditActorType.User, Result: AuditResult.Succeeded,
            OccurredAt: msg.UpdatedAt,
            UserId: msg.UserId,
            AgentId: null,
            VaultId: null,
            EntryId: null,
                        AgentName: null,
            ActorName: msg.DisplayName,
                        IpAddress: null,
            Metadata: new Dictionary<string, string>()), context.CancellationToken);
    }
}
