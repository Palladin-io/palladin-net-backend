using Palladin.Core.Events;
using Palladin.Core.Types;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Vault.Contracts.Events;

[PublicAPI]
public sealed record GrantRevokedEvent(
    Guid GrantId,
    Guid VaultId,
    Guid OrganizationId,
    Guid? AgentId,
    Guid? EntryId,
    GrantType Type,
    Guid? RevokedBy,
    bool RevokedBySystem,
    string AgentName,
    string? EntryLabel,
    string VaultName,
    string? ActorName,
    long DurationActiveSeconds,
    Instant UpdatedAt) : IIntegrationEvent;
