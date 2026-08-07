using Palladin.Core.Events;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Vault.Contracts.Events;

// A batch of entries was bulk-imported into a vault. Consumed by Analytics.
// The per-entry EntryUpsertedEvent still drives audit, search and onboarding. Carries the count,
// the source format label and the imported entry ids (never any entry content or secrets).
[PublicAPI]
public sealed record EntriesImportedEvent(
    Guid VaultId,
    Guid UserId,
    int Count,
    string Format,
    IReadOnlyList<Guid> EntryIds,
    Instant UpdatedAt) : IIntegrationEvent;
