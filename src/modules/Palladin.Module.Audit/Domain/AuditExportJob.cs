using Palladin.Core.Events;
using Palladin.Module.Audit.Contracts.Events;
using NodaTime;

namespace Palladin.Module.Audit.Domain;

// Tracks an async audit-log CSV export. Unlike AuditLogEntry this row is mutable (Pending -> Completed/
// Failed) and lives in its own table, while AuditLogEntry exposes no application mutation path.
internal sealed class AuditExportJob : EventEntityBase
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid RequestedBy { get; private set; }
    public AuditExportStatus Status { get; private set; }

    // Filters are stored exactly as received (CSV multi-select, mirroring the list endpoint) so the
    // generated CSV is 1:1 with the filtered list. EntryId stays single (drill-down).
    public string? VaultIds { get; private set; }
    public string? AgentIds { get; private set; }
    public string? UserIds { get; private set; }
    public Guid? EntryId { get; private set; }
    public string? EventTypes { get; private set; }
    public Instant? From { get; private set; }
    public Instant? To { get; private set; }

    public string? StorageKey { get; private set; }
    public int? RowCount { get; private set; }
    public string? Error { get; private set; }

    public Instant CreatedAt { get; private set; }
    public Instant? CompletedAt { get; private set; }
    public Instant? ExpiresAt { get; private set; }

    private AuditExportJob() { }

    internal static AuditExportJob Create(
        Guid id,
        Guid organizationId,
        Guid requestedBy,
        string requestedByName,
        string plan,
        string filtersUsed,
        string? vaultIds,
        string? agentIds,
        string? userIds,
        Guid? entryId,
        string? eventTypes,
        Instant? from,
        Instant? to,
        Instant now)
    {
        var job = new AuditExportJob
        {
            Id = id,
            OrganizationId = organizationId,
            RequestedBy = requestedBy,
            Status = AuditExportStatus.Pending,
            VaultIds = vaultIds,
            AgentIds = agentIds,
            UserIds = userIds,
            EntryId = entryId,
            EventTypes = eventTypes,
            From = from,
            To = to,
            CreatedAt = now,
        };

        job.AddEvent(new AuditLogExportRequestedEvent(id, organizationId, requestedBy, requestedByName, plan, filtersUsed, now));

        return job;
    }

    internal void MarkCompleted(string storageKey, int rowCount, Instant now, Duration downloadWindow)
    {
        Status = AuditExportStatus.Completed;
        StorageKey = storageKey;
        RowCount = rowCount;
        CompletedAt = now;
        ExpiresAt = now.Plus(downloadWindow);
    }

    internal void MarkFailed(string error, Instant now)
    {
        Status = AuditExportStatus.Failed;
        Error = error;
        CompletedAt = now;
    }

    internal bool IsDownloadable(Instant now) =>
        Status == AuditExportStatus.Completed && ExpiresAt is not null && now < ExpiresAt.Value;
}
