using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnCredentialAccessedAuditDefinition : ConsumerDefinition<OnCredentialAccessedAudit>
{
    public OnCredentialAccessedAuditDefinition() => EndpointName = VaultEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnCredentialAccessedAudit(IPublishEndpoint publishEndpoint) : IConsumer<CredentialAccessedEvent>
{
    public Task Consume(ConsumeContext<CredentialAccessedEvent> context)
    {
        var msg = context.Message;
        var metadata = new Dictionary<string, string>
        {
            ["grantId"] = msg.GrantId.ToString(),
            ["grantType"] = msg.Type.ToString(),
            ["method"] = msg.Method.ToString(),
        };
        if (msg.ExpiresAt is not null)
        {
            metadata["expiresAt"] = msg.ExpiresAt.Value.ToString();
        }

        if (msg.RemainingUses is not null)
        {
            metadata["remainingUses"] = msg.RemainingUses.Value.ToString();
        }

        return publishEndpoint.Publish(new AppendAuditLogCommand(
            OrganizationId: msg.OrganizationId,
            EventType: AuditEventType.CredentialAccessed,
            ActorType: AuditActorType.Agent, Result: AuditResult.Succeeded,
            OccurredAt: msg.UpdatedAt,
            UserId: null, AgentId: msg.AgentId, VaultId: msg.VaultId, EntryId: msg.EntryId,
            AgentName: msg.AgentName, ActorName: null, IpAddress: null, Metadata: metadata), context.CancellationToken);
    }
}
