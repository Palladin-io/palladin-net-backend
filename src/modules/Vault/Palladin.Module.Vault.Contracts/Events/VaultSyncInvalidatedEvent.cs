using JetBrains.Annotations;
using NodaTime;
using Palladin.Core.Events;

namespace Palladin.Module.Vault.Contracts.Events;

/// <summary>
/// Value-free hint that a committed Vault projection may have advanced.
/// Consumers must reconcile through the canonical Member sync endpoints.
/// </summary>
[PublicAPI]
public sealed record VaultSyncInvalidatedEvent(
    Guid OrganizationId,
    Guid VaultId,
    IReadOnlyList<Guid> MemberUserIds,
    ulong MemberSequence,
    ulong MutationVersion,
    Instant OccurredAt) : IIntegrationEvent;
