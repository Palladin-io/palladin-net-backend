using Palladin.Core.Events;
using Palladin.Core.Types;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Vault.Contracts.Events;

[PublicAPI]
public sealed record EntryUpsertedEvent(
    Guid OrganizationId,
    Guid VaultId,
    Guid EntryId,
    Guid UserId,
    EntityChange Change,
    ulong Revision,
    Instant UpdatedAt,
    bool ViaImport = false) : IIntegrationEvent;
