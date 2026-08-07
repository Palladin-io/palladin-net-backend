using Palladin.Core.Guid;
using Palladin.Core.Security;
using Palladin.Module.Audit.Domain;
using Palladin.Module.Audit.Infrastructure.Persistence;
using Palladin.Module.Identity.Contracts.ValueObjects;
using FastEndpoints;
using Hangfire;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using NodaTime;

namespace Palladin.Module.Audit.Features.Export;

// Same filter shape as ListAuditLogsRequest: CSV multi-select for vault/agent/user/eventType, single
// entryId (drill-down). Guarantees the exported CSV matches the filtered list 1:1.
[PublicAPI]
public sealed record RequestAuditLogExportRequest
{
    public string? VaultId { get; init; }
    public string? AgentId { get; init; }
    public string? UserId { get; init; }
    public Guid? EntryId { get; init; }
    public string? EventType { get; init; }
    public Instant? From { get; init; }
    public Instant? To { get; init; }
}

[PublicAPI]
public sealed record RequestAuditLogExportResponse(Guid JobId);

[PublicAPI]
public sealed record PlanUpgradeRequiredResponse(string Code, string Message);

[PublicAPI]
internal sealed class RequestAuditLogExportEndpoint(
    AuditDomainWriteContext domainWriteContext,
    IBackgroundJobClient backgroundJobClient,
    IGuidProvider guidProvider,
    IClock clock) : Endpoint<RequestAuditLogExportRequest, RequestAuditLogExportResponse>
{
    private const string PlanUpgradeRequiredCode = "plan-upgrade-required";

    public override void Configure()
    {
        Post("api/audit-logs/export");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.AuditView);
        Summary(summary =>
        {
            summary.Summary = "Request an audit-log CSV export";
            summary.Description = "Queues an async CSV export of the organization's audit log with the same filters as the list endpoint. Available on Pro plans and above. The download link is valid for 24h.";
        });
        Tags("Audit/AuditLogs");
    }

    public override async Task HandleAsync(RequestAuditLogExportRequest req, CancellationToken ct)
    {
        var userId = User.GetUserId();
        var organizationId = User.GetOrganizationId();
        if (userId is null || organizationId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var plan = Enum.TryParse<PlanType>(User.FindFirst(JwtClaimNames.Plan)?.Value, out var parsed)
            ? parsed
            : PlanType.Basic;

        if (plan < PlanType.Pro)
        {
            await Send.ResultAsync(Results.Json(
                new PlanUpgradeRequiredResponse(PlanUpgradeRequiredCode, "Audit log export requires the Pro plan or higher."),
                statusCode: StatusCodes.Status403Forbidden));
            return;
        }

        var filtersUsed = AuditLogQuery.DescribeFilters(
            AuditFilterParsing.ParseGuids(req.VaultId),
            AuditFilterParsing.ParseGuids(req.AgentId),
            AuditFilterParsing.ParseGuids(req.UserId),
            req.EntryId,
            AuditFilterParsing.ParseStrings(req.EventType),
            req.From, req.To);

        var job = AuditExportJob.Create(
            guidProvider.Generate(),
            organizationId.Value,
            userId.Value,
            User.GetDisplayName(),
            plan.ToString(),
            filtersUsed,
            req.VaultId,
            req.AgentId,
            req.UserId,
            req.EntryId,
            req.EventType,
            req.From,
            req.To,
            clock.GetCurrentInstant());

        domainWriteContext.Add(job);
        await domainWriteContext.CommitAsync(ct);

        backgroundJobClient.Enqueue<IAuditExportJobRunner>(runner => runner.RunAsync(job.Id, CancellationToken.None));

        await Send.ResponseAsync(new RequestAuditLogExportResponse(job.Id), StatusCodes.Status202Accepted, ct);
    }
}
