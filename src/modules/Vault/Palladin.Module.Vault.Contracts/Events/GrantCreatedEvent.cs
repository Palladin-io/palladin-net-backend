using Palladin.Core.Events;
using Palladin.Core.Types;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Vault.Contracts.Events;

// Vault-admin proactively created a grant (FULL or GRANULAR). Consumed by Analytics and Audit.
// EntryId/EntryLabel are populated for GRANULAR; null for FULL. Denormalized names are resolved at
// the emission site so consumers never re-resolve.
[PublicAPI]
public sealed record GrantCreatedEvent(
    Guid GrantId,
    Guid VaultId,
    Guid OrganizationId,
    Guid? AgentId,
    Guid? EntryId,
    GrantType Type,
    GrantStatus Status,
    string ExpirySource,
    Instant? ExpiresAt,
    int? QueryLimit,
    GrantMethods Methods,
    Guid? CreatedBy,
    string AgentName,
    string? EntryLabel,
    string VaultName,
    string? ActorName,
    Instant UpdatedAt) : IIntegrationEvent;
