using Palladin.Core.Events;
using JetBrains.Annotations;
using NodaTime;
using Palladin.Module.Audit.Contracts.ValueObjects;

namespace Palladin.Module.Audit.Contracts.Commands;

// OpenHost command: an owning module asks Audit to append one opaque audit row. Audit resolves
// nothing (no name lookups, no org-from-vault) — the publisher supplies opaque subject ids and the
// optional actor/agent display names needed for forensic attribution. One command covers every event (via
// EventType + Metadata, Open-Closed). OccurredAt + the natural key drive idempotency. Metadata is
// allow-listed non-sensitive context — NEVER vault/entry labels, request reasons, ciphertext, keys or secrets.
[PublicAPI]
public sealed record AppendAuditLogCommand(
    Guid OrganizationId,
    string EventType,
    AuditActorType ActorType,
    AuditResult Result,
    Instant OccurredAt,
    Guid? UserId,
    Guid? AgentId,
    Guid? VaultId,
    Guid? EntryId,
    string? AgentName,
    string? ActorName,
    string? IpAddress,
    IReadOnlyDictionary<string, string> Metadata) : IIntegrationCommand;
