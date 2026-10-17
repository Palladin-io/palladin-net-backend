using JetBrains.Annotations;
using NodaTime;
using Palladin.Core.Events;

namespace Palladin.Module.Notification.Contracts.Commands;

[PublicAPI]
public sealed record RecordEntryShareReceiptCommand(
    Guid OrganizationId,
    Guid SenderId,
    Guid ShareId,
    Guid VaultId,
    Guid EntryId,
    Instant OccurredAt) : IIntegrationCommand;
