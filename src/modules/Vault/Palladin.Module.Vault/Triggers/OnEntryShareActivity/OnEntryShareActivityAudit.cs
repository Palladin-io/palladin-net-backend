using System.Globalization;
using JetBrains.Annotations;
using MassTransit;
using Palladin.Core.Types;
using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.MassTransit;
using Palladin.Module.Vault.Infrastructure.Sharing;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnEntryShareActivityAuditDefinition : ConsumerDefinition<OnEntryShareActivityAudit>
{
    public OnEntryShareActivityAuditDefinition() => EndpointName = VaultEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnEntryShareActivityAudit(IPublishEndpoint publishEndpoint)
    : IConsumer<EntryShareActivityEvent>
{
    public Task Consume(ConsumeContext<EntryShareActivityEvent> context)
    {
        var message = context.Message;
        var (eventType, actorType) = message.Kind switch
        {
            EntryShareActivityKind.Created => (AuditEventType.EntryShareCreated, AuditActorType.User),
            EntryShareActivityKind.Delivered => (AuditEventType.EntryShareDelivered, AuditActorType.ExternalRecipient),
            EntryShareActivityKind.Confirmed => (AuditEventType.EntryShareConfirmed, AuditActorType.ExternalRecipient),
            EntryShareActivityKind.ProtectionChanged => (AuditEventType.EntryShareProtectionChanged, AuditActorType.User),
            EntryShareActivityKind.Expired => (AuditEventType.EntryShareExpired, AuditActorType.System),
            EntryShareActivityKind.RevokedBySender => (AuditEventType.EntryShareRevoked, AuditActorType.User),
            EntryShareActivityKind.EndedByRecipient => (AuditEventType.EntryShareEnded, AuditActorType.ExternalRecipient),
            EntryShareActivityKind.SourceAccessRemoved => (AuditEventType.EntryShareSourceAccessRemoved, AuditActorType.System),
            _ => throw new ArgumentOutOfRangeException(nameof(message.Kind)),
        };

        return publishEndpoint.Publish(new AppendAuditLogCommand(
            message.OrganizationId, eventType, actorType, AuditResult.Succeeded, message.UpdatedAt,
            actorType == AuditActorType.User ? message.SenderId : null,
            null, message.VaultId, message.EntryId, null, null, null,
            new Dictionary<string, string>
            {
                ["shareId"] = message.ShareId.ToString("D"),
                ["sequence"] = message.Sequence.ToString(CultureInfo.InvariantCulture),
            },
            EntryShareActivityIdentity.For(message.ShareId, message.Sequence)), context.CancellationToken);
    }
}
