using Palladin.Core.Events;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Vault.Contracts.Events;

[PublicAPI]
public sealed record CredentialFailureReportedEvent(
    Guid ReportId,
    Guid OrganizationId,
    Guid VaultId,
    Guid EntryId,
    Guid AgentId,
    string Code,
    Instant UpdatedAt) : IIntegrationEvent;
