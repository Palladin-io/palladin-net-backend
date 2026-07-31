using Palladin.Core.Events;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Vault.Contracts.Events;

// A one-time / use-limited grant reached its query limit and transitioned to Consumed.
// Consumed by Audit and Analytics. EntryId/EntryLabel are populated for GRANULAR grants
// (the consumed grant covered a single, known entry); null for FULL.
[PublicAPI]
public sealed record GrantConsumedEvent(
    Guid GrantId,
    Guid VaultId,
    Guid OrganizationId,
    Guid AgentId,
    Guid? EntryId,
    string? EntryLabel,
    Instant UpdatedAt) : IIntegrationEvent;
