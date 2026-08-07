using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnGrantDeniedAuditDefinition : ConsumerDefinition<OnGrantDeniedAudit>
{
    public OnGrantDeniedAuditDefinition() => EndpointName = VaultEndpoints.Audit;
}

[UsedImplicitly]
internal sealed class OnGrantDeniedAudit(IPublishEndpoint publishEndpoint) : IConsumer<GrantDeniedEvent>
{
    public Task Consume(ConsumeContext<GrantDeniedEvent> context)
    {
        var msg = context.Message;
        return publishEndpoint.Publish(new AppendAuditLogCommand(
            OrganizationId: msg.OrganizationId,
            EventType: AuditEventType.GrantDenied,
            ActorType: AuditActorType.User, Result: AuditResult.Denied,
            OccurredAt: msg.UpdatedAt,
            UserId: msg.DeniedBy, AgentId: msg.AgentId, VaultId: msg.VaultId, EntryId: msg.EntryId,
            AgentName: msg.AgentName, ActorName: msg.ActorName, IpAddress: null,
            Metadata: new Dictionary<string, string>
            {
                ["grantId"] = msg.GrantId.ToString(),
            }), context.CancellationToken);
    }
}
