using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnGrantApprovedAuditDefinition : ConsumerDefinition<OnGrantApprovedAudit>
{
    public OnGrantApprovedAuditDefinition() => EndpointName = VaultEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnGrantApprovedAudit(IPublishEndpoint publishEndpoint) : IConsumer<GrantApprovedEvent>
{
    public Task Consume(ConsumeContext<GrantApprovedEvent> context)
    {
        var msg = context.Message;
        var metadata = new Dictionary<string, string>
        {
            ["methods"] = msg.Methods.ToString(),
            ["grantId"] = msg.GrantId.ToString(),
            ["grantType"] = msg.Type.ToString(),
            ["expirySource"] = msg.ExpirySource,
        };
        if (msg.ExpiresAt is not null)
        {
            metadata["expiresAt"] = msg.ExpiresAt.Value.ToString();
        }

        if (msg.QueryLimit is not null)
        {
            metadata["queryLimit"] = msg.QueryLimit.Value.ToString();
        }

        return publishEndpoint.Publish(new AppendAuditLogCommand(
            OrganizationId: msg.OrganizationId,
            EventType: AuditEventType.GrantApproved,
            ActorType: AuditActorType.User, Result: AuditResult.Succeeded,
            OccurredAt: msg.UpdatedAt,
            UserId: msg.ApprovedBy, AgentId: msg.AgentId, VaultId: msg.VaultId, EntryId: msg.EntryId,
            AgentName: msg.AgentName, ActorName: msg.ActorName, IpAddress: null, Metadata: metadata), context.CancellationToken);
    }
}
