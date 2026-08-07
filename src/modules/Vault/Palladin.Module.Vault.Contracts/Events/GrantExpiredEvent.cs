using Palladin.Core.Events;
using Palladin.Core.Types;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Vault.Contracts.Events;

// A time-based grant expired because its TTL elapsed. Emitted by the expiry cron.
// Consumed by Audit and Analytics. Carries no ciphertext/key material. EntryId/EntryLabel are
// populated for GRANULAR grants (the expired grant covered a single, known entry); null for FULL.
[PublicAPI]
public sealed record GrantExpiredEvent(
    Guid GrantId,
    Guid VaultId,
    Guid OrganizationId,
    Guid? AgentId,
    Guid? EntryId,
    string? EntryLabel,
    GrantType Type,
    long? TtlSeconds,
    Instant UpdatedAt) : IIntegrationEvent;
