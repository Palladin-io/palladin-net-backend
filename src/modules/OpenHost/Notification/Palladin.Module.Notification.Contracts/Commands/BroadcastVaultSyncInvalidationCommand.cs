using JetBrains.Annotations;
using NodaTime;
using Palladin.Core.Events;

namespace Palladin.Module.Notification.Contracts.Commands;

/// <summary>
/// Delivers a value-free Vault synchronization hint to authorized realtime clients.
/// For an ordinary invalidation, Vault resolves recipients from committed membership
/// after handling the domain event. Tombstones carry an explicit former-Member snapshot.
/// </summary>
[PublicAPI]
public sealed record BroadcastVaultSyncInvalidationCommand(
    Guid OrganizationId,
    Guid VaultId,
    string MemberSequence,
    string MutationVersion,
    bool Removed,
    IReadOnlyList<Guid> RecipientUserIds,
    Instant OccurredAt) : IIntegrationCommand;
