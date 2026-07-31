using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnGrantExpiredAuditDefinition : ConsumerDefinition<OnGrantExpiredAudit>
{
    public OnGrantExpiredAuditDefinition() => EndpointName = VaultEndpoints.Audit;
}

[UsedImplicitly]
internal sealed class OnGrantExpiredAudit(IPublishEndpoint publishEndpoint) : IConsumer<GrantExpiredEvent>
{
    public Task Consume(ConsumeContext<GrantExpiredEvent> context)
    {
        var msg = context.Message;
        var metadata = new Dictionary<string, string>
        {
            ["grantId"] = msg.GrantId.ToString(),
            ["grantType"] = msg.Type.ToString(),
        };
        if (msg.TtlSeconds is not null)
        {
            metadata["ttlSeconds"] = msg.TtlSeconds.Value.ToString();
        }

        return publishEndpoint.Publish(new AppendAuditLogCommand(
            OrganizationId: msg.OrganizationId,
            EventType: AuditEventType.GrantExpired,
            ActorType: AuditActorType.System, Result: AuditResult.Succeeded,
            OccurredAt: msg.UpdatedAt,
            UserId: null, AgentId: msg.AgentId, VaultId: msg.VaultId, EntryId: msg.EntryId,
            AgentName: null, ActorName: null, IpAddress: null,
            Metadata: metadata), context.CancellationToken);
    }
}
