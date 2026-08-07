using Palladin.Core.Events;
using Palladin.Core.Types;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Vault.Contracts.Events;

// User approved a pending grant. Consumed by Notification (SignalR/push), Audit and Analytics.
// Critical funnel event: Agent Enrolled -> Grant Approved -> Credential Accessed.
[PublicAPI]
public sealed record GrantApprovedEvent(
    Guid GrantId,
    Guid VaultId,
    Guid OrganizationId,
    Guid AgentId,
    Guid? EntryId,
    Guid ApprovedBy,
    GrantType Type,
    string AgentName,
    string? EntryLabel,
    string VaultName,
    string? ActorName,
    string ExpirySource,
    Instant? ExpiresAt,
    int? QueryLimit,
    GrantMethods Methods,
    Instant UpdatedAt) : IIntegrationEvent;
