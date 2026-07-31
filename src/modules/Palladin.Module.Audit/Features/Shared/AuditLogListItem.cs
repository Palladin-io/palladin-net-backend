using Palladin.Module.Audit.Domain;
using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Audit.Features;

// Audit list item — no secrets/ciphertext, ids and non-sensitive context only.
[PublicAPI]
public sealed record AuditLogListItem(
    Guid Id,
    string EventType,
    AuditActorType ActorType,
    AuditResult Result,
    Guid? UserId,
    Guid? AgentId,
    Guid? VaultId,
    Guid? EntryId,
    string? AgentName,
    string? ActorName,
    IReadOnlyDictionary<string, string> Metadata,
    Instant OccurredAt,
    Instant CreatedAt);
