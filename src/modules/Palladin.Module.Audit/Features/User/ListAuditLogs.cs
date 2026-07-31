using Palladin.Core.Api;
using Palladin.Core.Events;
using Palladin.Core.Security;
using Palladin.Module.Audit.Contracts.Events;
using Palladin.Module.Audit.Infrastructure.Persistence;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Palladin.Module.Audit.Features;

[PublicAPI]
public sealed record ListAuditLogsRequest
{
    public string? VaultId { get; init; }
    public string? AgentId { get; init; }
    public string? UserId { get; init; }
    public Guid? EntryId { get; init; }
    public string? EventType { get; init; }
    public Instant? From { get; init; }
    public Instant? To { get; init; }
    public string? Cursor { get; init; }
    public int? PageSize { get; init; }
}

[PublicAPI]
public sealed record ListAuditLogsResponse(IReadOnlyList<AuditLogListItem> Items, string? NextCursor);

[UsedImplicitly]
internal sealed class ListAuditLogsValidator : Validator<ListAuditLogsRequest>
{
    public ListAuditLogsValidator()
    {
        RuleFor(x => x.PageSize!.Value).InclusiveBetween(1, 100).When(x => x.PageSize is not null);
    }
}

[PublicAPI]
internal sealed class ListAuditLogsEndpoint(
    AuditDomainReadContext domainReadContext,
    IEnumerable<IEventPublisher> eventPublishers,
    IClock clock) : Endpoint<ListAuditLogsRequest, ListAuditLogsResponse>
{
    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 100;

    public override void Configure()
    {
        Get("api/audit-logs");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.AuditView);
        Summary(summary =>
        {
            summary.Summary = "List organization audit logs";
            summary.Description = "Returns audit events for the caller's organization, newest-first, with cursor pagination and filters. No secrets or ciphertext are included.";
        });
        Tags("Audit/AuditLogs");
    }

    public override async Task HandleAsync(ListAuditLogsRequest req, CancellationToken ct)
    {
        var userId = User.GetUserId()!.Value;
        var organizationId = User.GetOrganizationId()!.Value;
        var pageSize = Math.Min(req.PageSize ?? DefaultPageSize, MaxPageSize);
        var cursor = InstantCursor.Decode(req.Cursor);

        var vaultIds = AuditFilterParsing.ParseGuids(req.VaultId);
        var agentIds = AuditFilterParsing.ParseGuids(req.AgentId);
        var userIds = AuditFilterParsing.ParseGuids(req.UserId);
        var eventTypes = AuditFilterParsing.ParseStrings(req.EventType);

        var query = domainReadContext.AuditLogEntries.Where(e => e.OrganizationId == organizationId);
        query = AuditLogQuery.ApplyFilters(query, vaultIds, agentIds, userIds, req.EntryId, eventTypes, req.From, req.To);
        var (items, nextCursor) = await AuditLogQuery.PageNewestFirstAsync(query, cursor, pageSize, ct);

        // Analytics flow per CLAUDE.md: domain event -> MassTransit trigger -> analytics publish.
        // Never call analyticsService.CaptureEvent directly from an endpoint.
        var filtersUsed = AuditLogQuery.DescribeFilters(vaultIds, agentIds, userIds, req.EntryId, eventTypes, req.From, req.To);
        foreach (var publisher in eventPublishers)
        {
            await publisher.PublishAsync(
                new AuditLogsQueriedEvent(userId, organizationId, filtersUsed, clock.GetCurrentInstant()),
                ct);
        }

        await Send.OkAsync(new ListAuditLogsResponse(items, nextCursor), ct);
    }
}
