using JetBrains.Annotations;
using NodaTime;
using Palladin.Core.Types;

namespace Palladin.Module.Vault.Shared;

[PublicAPI]
public sealed record MemberSyncItem(
    Guid EntryId,
    string Kind,
    EntryState? State,
    Instant? UpdatedAt,
    string? CurrentRevision,
    string? MemberIndexRevision,
    uint? CurrentKeyVersion,
    VaultEntryKeyContract? EntryKey,
    MemberIndexEnvelopeContract? MemberIndex);

[PublicAPI]
public sealed record AgentDiscoverySyncItem(
    Guid EntryId,
    string Kind,
    string? AgentDiscoveryRevision,
    AgentDiscoveryEnvelopeContract? AgentDiscovery);

[PublicAPI]
public sealed record MemberSnapshotResponse(
    string SnapshotBaseSequence,
    IReadOnlyList<MemberSyncItem> Items,
    string? NextCursor);

[PublicAPI]
public sealed record AgentDiscoverySnapshotResponse(
    string SnapshotBaseSequence,
    IReadOnlyList<AgentDiscoverySyncItem> Items,
    string? NextCursor);

[PublicAPI]
public sealed record MemberDeltaResponse(
    string DeltaUpperBound,
    string AppliedThroughSequence,
    IReadOnlyList<MemberSyncItem> Items,
    string? ContinuationCursor);

[PublicAPI]
public sealed record AgentDiscoveryDeltaResponse(
    string DeltaUpperBound,
    string AppliedThroughSequence,
    IReadOnlyList<AgentDiscoverySyncItem> Items,
    string? ContinuationCursor);

[PublicAPI]
public sealed record VaultSyncResetResponse(
    string Outcome,
    string CurrentSequence,
    string MinRetainedSequence,
    bool NewSnapshotRequired);

[PublicAPI]
public sealed record VaultSyncErrorResponse(string Outcome);
