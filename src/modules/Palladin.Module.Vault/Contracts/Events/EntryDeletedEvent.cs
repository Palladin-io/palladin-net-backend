using Palladin.Core.Events;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Vault.Contracts.Events;

[PublicAPI]
public sealed record EntryDeletedEvent(
    Guid EntryId,
    Guid VaultId,
    Guid DeletedBy,
    string ActorName,
    string EntryLabel,
    Instant DeletedAt) : IIntegrationEvent;
