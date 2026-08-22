using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnGrantRevokedAuditDefinition : ConsumerDefinition<OnGrantRevokedAudit>
{
    public OnGrantRevokedAuditDefinition() => EndpointName = VaultEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnGrantRevokedAudit(IPublishEndpoint publishEndpoint) : IConsumer<GrantRevokedEvent>
{
    public Task Consume(ConsumeContext<GrantRevokedEvent> context)
    {
        var msg = context.Message;
        return publishEndpoint.Publish(new AppendAuditLogCommand(
            OrganizationId: msg.OrganizationId,
            EventType: AuditEventType.GrantRevoked,
            ActorType: msg.RevokedBySystem ? AuditActorType.System : AuditActorType.User, Result: AuditResult.Succeeded,
            OccurredAt: msg.UpdatedAt,
            UserId: msg.RevokedBy, AgentId: msg.AgentId, VaultId: msg.VaultId, EntryId: msg.EntryId,
            AgentName: msg.AgentName, ActorName: msg.ActorName, IpAddress: null,
            Metadata: new Dictionary<string, string>
            {
                ["grantId"] = msg.GrantId.ToString(),
                ["grantType"] = msg.Type.ToString(),
                ["durationActiveSeconds"] = msg.DurationActiveSeconds.ToString(),
                ["bySystem"] = msg.RevokedBySystem.ToString(),
            }), context.CancellationToken);
    }
}
