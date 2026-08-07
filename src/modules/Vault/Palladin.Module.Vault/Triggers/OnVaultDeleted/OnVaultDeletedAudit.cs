using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnVaultDeletedAuditDefinition : ConsumerDefinition<OnVaultDeletedAudit>
{
    public OnVaultDeletedAuditDefinition() => EndpointName = VaultEndpoints.Audit;
}

[UsedImplicitly]
internal sealed class OnVaultDeletedAudit(IPublishEndpoint publishEndpoint) : IConsumer<VaultDeletedEvent>
{
    public Task Consume(ConsumeContext<VaultDeletedEvent> context)
    {
        var msg = context.Message;
        return publishEndpoint.Publish(new AppendAuditLogCommand(
            OrganizationId: msg.OrganizationId,
            EventType: AuditEventType.VaultDeleted,
            ActorType: AuditActorType.User, Result: AuditResult.Succeeded,
            OccurredAt: msg.UpdatedAt,
            UserId: msg.UserId, AgentId: null, VaultId: msg.VaultId, EntryId: null, AgentName: null, ActorName: msg.ActorName, IpAddress: null,
            Metadata: new Dictionary<string, string>
            {
                ["memberCount"] = msg.MemberCount.ToString(),
            }), context.CancellationToken);
    }
}
