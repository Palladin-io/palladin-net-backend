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
internal sealed class OnVaultUpsertedAuditDefinition : ConsumerDefinition<OnVaultUpsertedAudit>
{
    public OnVaultUpsertedAuditDefinition() => EndpointName = VaultEndpoints.Self;
}

// Vault owns vaults, so it (not Audit) denormalizes the audit row and publishes the append command.
// The change classifier maps to VaultCreated / VaultUpdated; creation uses a fixed sentinel timestamp
// so the idempotency key stays stable across redeliveries.
[UsedImplicitly]
internal sealed class OnVaultUpsertedAudit(IPublishEndpoint publishEndpoint) : IConsumer<VaultUpsertedEvent>
{
    public Task Consume(ConsumeContext<VaultUpsertedEvent> context)
    {
        var msg = context.Message;
        var command = msg.Change == EntityChange.Created
            ? new AppendAuditLogCommand(
                OrganizationId: msg.OrganizationId,
                EventType: AuditEventType.VaultCreated,
                ActorType: AuditActorType.User, Result: AuditResult.Succeeded,
                OccurredAt: Instant.FromUnixTimeSeconds(0),
                UserId: msg.UserId, AgentId: null, VaultId: msg.VaultId, EntryId: null, AgentName: null, ActorName: msg.ActorName, IpAddress: null,
                Metadata: new Dictionary<string, string>())
            : new AppendAuditLogCommand(
                OrganizationId: msg.OrganizationId,
                EventType: AuditEventType.VaultUpdated,
                ActorType: AuditActorType.User, Result: AuditResult.Succeeded,
                OccurredAt: msg.UpdatedAt,
                UserId: msg.UserId, AgentId: null, VaultId: msg.VaultId, EntryId: null, AgentName: null, ActorName: msg.ActorName, IpAddress: null,
                Metadata: new Dictionary<string, string>());

        return publishEndpoint.Publish(command, context.CancellationToken);
    }
}
