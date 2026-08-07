using Palladin.Core.Events;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Audit.Contracts.Events;

// An operator queried the audit log list scoped to a single vault (Vault Detail audit tab). Consumed
// by Analytics only.
[PublicAPI]
public sealed record VaultAuditLogsQueriedEvent(
    Guid UserId,
    Guid VaultId,
    string FiltersUsed,
    int ResultCount,
    Instant UpdatedAt) : IIntegrationEvent;
