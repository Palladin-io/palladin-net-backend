using Palladin.Core.Events;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Audit.Contracts.Events;

// An operator queried the organization-wide audit log list. Consumed by Analytics only.
[PublicAPI]
public sealed record AuditLogsQueriedEvent(
    Guid UserId,
    Guid OrganizationId,
    string FiltersUsed,
    Instant UpdatedAt) : IIntegrationEvent;
