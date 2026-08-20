using JetBrains.Annotations;
using NodaTime;
using Palladin.Core.Events;

namespace Palladin.Module.Notification.Contracts.Commands;

/// <summary>
/// Delivers a value-free Vault synchronization hint to authorized realtime clients.
/// Recipients are the authoritative Member snapshot captured by the committed
/// Vault event, or the explicit former Member for an access-removal tombstone.
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
