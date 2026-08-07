using Palladin.Core.Events;
using Palladin.Core.Types;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Vault.Contracts.Events;

[PublicAPI]
public sealed record VaultUpsertedEvent(
    Guid VaultId,
    Guid OrganizationId,
    Guid UserId,
    string ActorName,
    bool IsDefault,
    EntityChange Change,
    Instant UpdatedAt) : IIntegrationEvent;
