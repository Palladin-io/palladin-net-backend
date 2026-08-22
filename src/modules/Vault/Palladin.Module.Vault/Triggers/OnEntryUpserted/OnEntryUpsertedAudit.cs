using Palladin.Core.Types;
using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;
using NodaTime;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnEntryUpsertedAuditDefinition : ConsumerDefinition<OnEntryUpsertedAudit>
{
    public OnEntryUpsertedAuditDefinition() => EndpointName = VaultEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnEntryUpsertedAudit(
    IPublishEndpoint publishEndpoint) : IConsumer<EntryUpsertedEvent>
{
    public async Task Consume(ConsumeContext<EntryUpsertedEvent> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        var command = msg.Change == EntityChange.Created
            ? new AppendAuditLogCommand(
                OrganizationId: msg.OrganizationId,
                EventType: AuditEventType.EntryCreated,
                ActorType: AuditActorType.User, Result: AuditResult.Succeeded,
                OccurredAt: msg.UpdatedAt,
                UserId: msg.UserId, AgentId: null, VaultId: msg.VaultId, EntryId: msg.EntryId,
                AgentName: null, ActorName: null, IpAddress: null,
                Metadata: new Dictionary<string, string> { ["revision"] = msg.Revision.ToString() })
            : new AppendAuditLogCommand(
                OrganizationId: msg.OrganizationId,
                EventType: AuditEventType.EntryUpdated,
                ActorType: AuditActorType.User, Result: AuditResult.Succeeded,
                OccurredAt: msg.UpdatedAt,
                UserId: msg.UserId, AgentId: null, VaultId: msg.VaultId, EntryId: msg.EntryId,
                AgentName: null, ActorName: null, IpAddress: null,
                Metadata: new Dictionary<string, string> { ["revision"] = msg.Revision.ToString() });

        await publishEndpoint.Publish(command, ct);
    }
}
