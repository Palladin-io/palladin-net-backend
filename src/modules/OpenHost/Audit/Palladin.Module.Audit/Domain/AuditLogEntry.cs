using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;
using NodaTime;

namespace Palladin.Module.Audit.Domain;

// Append-only audit record. There are intentionally NO mutating methods — the only way to change the
// table is to insert. Update/delete are not exposed at any layer (no UPDATE/DELETE endpoints, the
// read context throws on SaveChanges). Per the persistence boundary, append-only is enforced in the
// domain/application layer; PostgreSQL migrations contain no triggers or stored functions.
internal sealed class AuditLogEntry
{
    public Guid Id { get; private set; }
    public bool HasExplicitOccurrenceId { get; private set; }
    public Guid OrganizationId { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public AuditActorType ActorType { get; private set; }
    public AuditResult Result { get; private set; }
    public Guid? UserId { get; private set; }
    public Guid? AgentId { get; private set; }
    public Guid? VaultId { get; private set; }
    public Guid? EntryId { get; private set; }

    // Names denormalized at write time (the Audit module keeps no replica of every module). AgentName
    // is the agent involved in the row; ActorName is the human operator behind a user-actor event.
    // Null when the source event does not carry the name; clients fall back to their own resolution.
    public string? AgentName { get; private set; }
    public string? ActorName { get; private set; }

    public string? IpAddress { get; private set; }

    // Allow-listed, non-sensitive structural context (ids/types/ttl/mode). NEVER user-provided text,
    // vault/entry labels, request reasons, ciphertext, keys or secrets.
    public IReadOnlyDictionary<string, string> Metadata { get; private set; } = new Dictionary<string, string>();

    public Instant CreatedAt { get; private set; }

    // Timestamp carried by the source event — used together with the natural key for idempotency, so
    // a MassTransit redelivery does not create a duplicate audit row.
    public Instant OccurredAt { get; private set; }

    private AuditLogEntry() { }

    internal static AuditLogEntry Create(
        Guid id,
        Guid organizationId,
        string eventType,
        AuditActorType actorType,
        AuditResult result,
        Instant occurredAt,
        Instant now,
        Guid? userId = null,
        Guid? agentId = null,
        Guid? vaultId = null,
        Guid? entryId = null,
        string? agentName = null,
        string? actorName = null,
        string? ipAddress = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        bool hasExplicitOccurrenceId = false) =>
        new()
        {
            Id = id,
            HasExplicitOccurrenceId = hasExplicitOccurrenceId,
            OrganizationId = organizationId,
            EventType = eventType,
            ActorType = actorType,
            Result = result,
            UserId = userId,
            AgentId = agentId,
            VaultId = vaultId,
            EntryId = entryId,
            AgentName = agentName,
            ActorName = actorName,
            IpAddress = ipAddress,
            Metadata = metadata ?? new Dictionary<string, string>(),
            OccurredAt = occurredAt,
            CreatedAt = now,
        };
}
