using Palladin.Core.Api;
using Palladin.Module.Audit.Domain;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Palladin.Module.Audit.Features;

// Shared filter and newest-first cursor pagination for both organization-scoped and vault-scoped
// audit endpoints.
internal static class AuditLogQuery
{
    internal static IQueryable<AuditLogEntry> ApplyFilters(
        IQueryable<AuditLogEntry> query,
        IReadOnlyCollection<Guid> vaultIds,
        IReadOnlyCollection<Guid> agentIds,
        IReadOnlyCollection<Guid> userIds,
        Guid? entryId,
        IReadOnlyCollection<string> eventTypes,
        Instant? from,
        Instant? to)
    {
        if (vaultIds.Count > 0)
        {
            query = query.Where(e => e.VaultId != null && vaultIds.Contains(e.VaultId.Value));
        }

        if (agentIds.Count > 0)
        {
            query = query.Where(e => e.AgentId != null && agentIds.Contains(e.AgentId.Value));
        }

        if (userIds.Count > 0)
        {
            query = query.Where(e => e.UserId != null && userIds.Contains(e.UserId.Value));
        }

        if (entryId is not null)
        {
            query = query.Where(e => e.EntryId == entryId);
        }

        if (eventTypes.Count > 0)
        {
            query = query.Where(e => eventTypes.Contains(e.EventType));
        }

        if (from is not null)
        {
            query = query.Where(e => e.CreatedAt >= from.Value);
        }

        if (to is not null)
        {
            query = query.Where(e => e.CreatedAt <= to.Value);
        }

        return query;
    }

    internal static async Task<(IReadOnlyList<AuditLogListItem> Items, string? NextCursor)> PageNewestFirstAsync(
        IQueryable<AuditLogEntry> query,
        InstantCursor? cursor,
        int pageSize,
        CancellationToken ct)
    {
        if (cursor is not null)
        {
            query = query.Where(e =>
                e.CreatedAt < cursor.Timestamp
                || (e.CreatedAt == cursor.Timestamp && e.Id.CompareTo(cursor.Id) < 0));
        }

        var rows = await query
            .OrderByDescending(e => e.CreatedAt)
            .ThenByDescending(e => e.Id)
            .Take(pageSize + 1)
            .Select(e => new AuditLogListItem(
                e.Id,
                e.EventType,
                e.ActorType,
                e.Result,
                e.UserId,
                e.AgentId,
                e.VaultId,
                e.EntryId,
                e.AgentName,
                e.ActorName,
                e.Metadata,
                e.OccurredAt,
                e.CreatedAt))
            .ToListAsync(ct);

        string? nextCursor = null;
        if (rows.Count > pageSize)
        {
            var last = rows[pageSize - 1];
            nextCursor = InstantCursor.Encode(last.CreatedAt, last.Id);
            rows = rows.Take(pageSize).ToList();
        }

        return (rows, nextCursor);
    }

    internal static string DescribeFilters(
        IReadOnlyCollection<Guid> vaultIds,
        IReadOnlyCollection<Guid> agentIds,
        IReadOnlyCollection<Guid> userIds,
        Guid? entryId,
        IReadOnlyCollection<string> eventTypes,
        Instant? from,
        Instant? to)
    {
        var used = new List<string>();
        if (vaultIds.Count > 0) { used.Add(Describe("vault", vaultIds.Count)); }
        if (agentIds.Count > 0) { used.Add(Describe("agent", agentIds.Count)); }
        if (userIds.Count > 0) { used.Add(Describe("user", userIds.Count)); }
        if (entryId is not null) { used.Add("entry"); }
        if (eventTypes.Count > 0) { used.Add(Describe("eventType", eventTypes.Count)); }
        if (from is not null || to is not null) { used.Add("dateRange"); }
        return used.Count == 0 ? "none" : string.Join(",", used);
    }

    private static string Describe(string name, int count) => count > 1 ? $"{name}({count})" : name;
}
