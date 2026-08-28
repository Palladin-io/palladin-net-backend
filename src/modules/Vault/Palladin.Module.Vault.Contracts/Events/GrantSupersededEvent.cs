using Palladin.Core.Events;
using Palladin.Core.Types;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Vault.Contracts.Events;

[PublicAPI]
public sealed record GrantSupersededEvent(
    Guid GrantId,
    Guid SupersededByGrantId,
    Guid VaultId,
    Guid OrganizationId,
    Guid AgentId,
    Guid EntryId,
    GrantType Type,
    string AgentName,
    string? EntryLabel,
    string VaultName,
    long DurationActiveSeconds,
    Instant UpdatedAt) : IIntegrationEvent;
