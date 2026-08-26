using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnGrantSupersededAuditDefinition : ConsumerDefinition<OnGrantSupersededAudit>
{
    public OnGrantSupersededAuditDefinition() => EndpointName = VaultEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnGrantSupersededAudit(IPublishEndpoint publishEndpoint) : IConsumer<GrantSupersededEvent>
{
    public Task Consume(ConsumeContext<GrantSupersededEvent> context)
    {
        var msg = context.Message;
        return publishEndpoint.Publish(new AppendAuditLogCommand(
            OrganizationId: msg.OrganizationId,
            EventType: AuditEventType.GrantSuperseded,
            ActorType: AuditActorType.System,
            Result: AuditResult.Succeeded,
            OccurredAt: msg.UpdatedAt,
            UserId: null,
            AgentId: msg.AgentId,
            VaultId: msg.VaultId,
            EntryId: msg.EntryId,
            AgentName: msg.AgentName,
            ActorName: null,
            IpAddress: null,
            Metadata: new Dictionary<string, string>
            {
                ["grantId"] = msg.GrantId.ToString(),
                ["supersededByGrantId"] = msg.SupersededByGrantId.ToString(),
                ["grantType"] = Palladin.Core.Types.GrantType.Granular.ToString(),
                ["durationActiveSeconds"] = msg.DurationActiveSeconds.ToString(),
            }), context.CancellationToken);
    }
}
