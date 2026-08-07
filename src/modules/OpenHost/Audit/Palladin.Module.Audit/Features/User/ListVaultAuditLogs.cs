using Palladin.Core.Api;
using Palladin.Core.Events;
using Palladin.Core.Security;
using Palladin.Module.Audit.Contracts.Events;
using Palladin.Module.Audit.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using NodaTime;

namespace Palladin.Module.Audit.Features;

[PublicAPI]
public sealed record ListVaultAuditLogsRequest
{
    public Guid VaultId { get; init; }
    public string? Actions { get; init; }
    public string? EventType { get; init; }
    public string? AgentId { get; init; }
    public string? UserId { get; init; }
    public Guid? EntryId { get; init; }
    public Instant? From { get; init; }
    public Instant? To { get; init; }
    public string? Cursor { get; init; }
    public int? PageSize { get; init; }
}

[UsedImplicitly]
internal sealed class ListVaultAuditLogsValidator : Validator<ListVaultAuditLogsRequest>
{
    public ListVaultAuditLogsValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.PageSize!.Value).InclusiveBetween(1, 100).When(x => x.PageSize is not null);
    }
}

[PublicAPI]
internal sealed class ListVaultAuditLogsEndpoint(
    AuditDomainReadContext domainReadContext,
    IVaultDirectory vaultDirectory,
    IEnumerable<IEventPublisher> eventPublishers,
    IClock clock) : Endpoint<ListVaultAuditLogsRequest, ListAuditLogsResponse>
{
    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 100;

    public override void Configure()
    {
        Get("api/vaults/{vaultId:guid}/audit-logs");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.AuditView);
        Summary(summary =>
        {
            summary.Summary = "List a vault's audit logs";
            summary.Description = "Returns audit events scoped to a single vault, newest-first, for the Vault Detail audit tab. Caller must be a member of the vault.";
        });
        Tags("Audit/AuditLogs");
    }

    public override async Task HandleAsync(ListVaultAuditLogsRequest req, CancellationToken ct)
    {
        var userId = User.GetUserId()!.Value;

        var organizationId = await vaultDirectory.GetOrganizationIdAsync(req.VaultId, ct);
        if (organizationId is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (!await vaultDirectory.IsMemberAsync(req.VaultId, userId, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var pageSize = Math.Min(req.PageSize ?? DefaultPageSize, MaxPageSize);
        var cursor = InstantCursor.Decode(req.Cursor);

        // `actions` (legacy) and `eventType` both carry CSV event types — merged into one IN filter.
        var eventTypes = AuditFilterParsing.ParseStrings(req.Actions, req.EventType);
        var agentIds = AuditFilterParsing.ParseGuids(req.AgentId);
        var userIds = AuditFilterParsing.ParseGuids(req.UserId);

        // Defense-in-depth: filter by OrganizationId in addition to VaultId so a future bug in the
        // membership check can't widen the blast radius across orgs. Also makes the existing
        // (OrganizationId, CreatedAt, Id) index reachable for this query shape.
        var query = domainReadContext.AuditLogEntries.Where(e =>
            e.OrganizationId == organizationId.Value && e.VaultId == req.VaultId);
        query = AuditLogQuery.ApplyFilters(query, vaultIds: [], agentIds, userIds, req.EntryId, eventTypes, req.From, req.To);

        var (items, nextCursor) = await AuditLogQuery.PageNewestFirstAsync(query, cursor, pageSize, ct);

        // Analytics flow per CLAUDE.md: domain event -> MassTransit trigger -> analytics publish.
        // Never call analyticsService.CaptureEvent directly from an endpoint.
        var filtersUsed = AuditLogQuery.DescribeFilters([], agentIds, userIds, req.EntryId, eventTypes, req.From, req.To);
        foreach (var publisher in eventPublishers)
        {
            await publisher.PublishAsync(
                new VaultAuditLogsQueriedEvent(userId, req.VaultId, filtersUsed, items.Count, clock.GetCurrentInstant()),
                ct);
        }

        await Send.OkAsync(new ListAuditLogsResponse(items, nextCursor), ct);
    }
}
