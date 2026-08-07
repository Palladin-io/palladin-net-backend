using Palladin.Core.Events;
using Palladin.Core.Types;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Vault.Contracts.Events;

// A credential was successfully delivered to an agent. Consumed by Audit, Analytics and Notification.
// Terminal funnel event: Grant Approved -> Credential Accessed. Carries NO ciphertext/key material.
// Names (AgentName / EntryLabel / VaultName) are denormalized at write time so consumers (notifications,
// audit) render "who accessed what" without resolving ids — empty string when unknown.
// Expiry is type-dependent and reported as flat nullable fields (JSON-safe, no polymorphism): a
// time-based grant sets ExpiresAt, a use-based grant sets RemainingUses; exactly one is non-null.
[PublicAPI]
public sealed record CredentialAccessedEvent(
    Guid GrantId,
    Guid VaultId,
    Guid OrganizationId,
    Guid AgentId,
    Guid EntryId,
    GrantType Type,
    string AgentName,
    string EntryLabel,
    string VaultName,
    Instant? ExpiresAt,
    int? RemainingUses,
    GrantMethods Method,
    Instant UpdatedAt) : IIntegrationEvent
{
    // Fallbacks used when a denormalized name cannot be resolved (deleted/missing replica row).
    public const string UnknownAgent = "Unknown agent";
    public const string UnknownEntry = "a credential";
}
