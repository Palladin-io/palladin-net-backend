using Palladin.Core.Events;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Vault.Contracts.Events;

// An agent's credential delivery attempt was denied. Consumed by Audit and Analytics.
// Reason is a short machine code (no_active_grant / expired / material_unavailable / query_limit).
[PublicAPI]
public sealed record CredentialAccessDeniedEvent(
    Guid VaultId,
    Guid AgentId,
    Guid EntryId,
    string Reason,
    Instant UpdatedAt) : IIntegrationEvent;
