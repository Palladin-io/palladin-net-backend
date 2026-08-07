using Palladin.Core.Events;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Audit.Contracts.Events;

// Raised when an audit-log CSV export is requested. Consumed inside the Audit module by the analytics
// trigger (be:audit:export-requested) and the audit-trail trigger (audit.export.requested). Carries only
// non-sensitive metadata — never credential values or secrets.
[PublicAPI]
public sealed record AuditLogExportRequestedEvent(
    Guid JobId,
    Guid OrganizationId,
    Guid RequestedBy,
    string RequestedByName,
    string Plan,
    string FiltersUsed,
    Instant RequestedAt) : IIntegrationEvent;
