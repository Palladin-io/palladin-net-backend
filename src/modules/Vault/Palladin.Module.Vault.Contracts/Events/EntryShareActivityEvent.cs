using JetBrains.Annotations;
using NodaTime;
using Palladin.Core.Events;
using Palladin.Core.Types;

namespace Palladin.Module.Vault.Contracts.Events;

[PublicAPI]
public sealed record EntryShareActivityEvent(
    Guid OrganizationId,
    Guid VaultId,
    Guid EntryId,
    Guid ShareId,
    Guid SenderId,
    long Sequence,
    EntryShareActivityKind Kind,
    bool NotifySender,
    Instant UpdatedAt) : IIntegrationEvent;
