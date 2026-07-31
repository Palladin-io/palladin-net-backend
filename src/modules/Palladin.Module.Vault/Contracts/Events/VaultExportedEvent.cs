using Palladin.Core.Events;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Vault.Contracts.Events;

// A vault member exported the vault's entries client-side. This is an audit/analytics signal only — the
// server never sees the exported plaintext. Carries the format and how many entries were exported (the
// count is a client-reported figure), never any entry content. Consumed by Audit and Analytics.
[PublicAPI]
public sealed record VaultExportedEvent(
    Guid VaultId,
    Guid UserId,
    string ActorName,
    string Format,
    int EntryCount,
    Instant UpdatedAt) : IIntegrationEvent;
