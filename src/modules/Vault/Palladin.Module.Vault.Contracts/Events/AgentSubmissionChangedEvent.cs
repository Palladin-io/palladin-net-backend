using JetBrains.Annotations;
using NodaTime;
using Palladin.Core.Events;
using Palladin.Core.Types;

namespace Palladin.Module.Vault.Contracts.Events;

// Structural Agent Entry submission lifecycle fact. It never carries labels, hosts, usernames,
// ciphertext or wrapped keys; the submitting Agent and the deciding Member stay distinct.
[PublicAPI]
public sealed record AgentSubmissionChangedEvent(
    Guid OrganizationId,
    Guid SubmissionId,
    Guid AgentId,
    AgentSubmissionKind SubmissionKind,
    AgentSubmissionEntryType EntryType,
    Guid? TargetVaultId,
    Guid? TargetEntryId,
    AgentSubmissionAction Action,
    AgentSubmissionState State,
    Guid? DecidedBy,
    Guid? DecisionVaultId,
    Guid? DecisionEntryId,
    Guid? DecisionGrantId,
    Instant OccurredAt) : IIntegrationEvent;
