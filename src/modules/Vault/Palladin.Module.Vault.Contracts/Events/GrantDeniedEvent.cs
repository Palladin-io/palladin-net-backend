using Palladin.Core.Events;
using Palladin.Core.Types;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Vault.Contracts.Events;

// User denied a pending grant. Consumed by Audit and Analytics.
[PublicAPI]
public sealed record GrantDeniedEvent(
    Guid GrantId,
    Guid VaultId,
    Guid OrganizationId,
    Guid AgentId,
    Guid DeniedBy,
    Guid EntryId,
    string AgentName,
    string EntryLabel,
    string VaultName,
    string? ActorName,
    GrantMethods Methods,
    Instant UpdatedAt) : IIntegrationEvent;
