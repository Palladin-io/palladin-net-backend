using Palladin.Core.Security;
using Palladin.Module.Audit.Infrastructure.Persistence;
using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Palladin.Module.Audit.Features.Export;

[PublicAPI]
public sealed record GetAuditLogExportRequest
{
    public Guid JobId { get; init; }
}

[PublicAPI]
public sealed record GetAuditLogExportResponse(
    Guid JobId,
    string Status,
    int? RowCount,
    Instant RequestedAt,
    Instant? ExpiresAt,
    bool Downloadable);

[PublicAPI]
internal sealed class GetAuditLogExportEndpoint(
    AuditDomainReadContext domainReadContext,
    IClock clock) : Endpoint<GetAuditLogExportRequest, GetAuditLogExportResponse>
{
    public override void Configure()
    {
        Get("api/audit-logs/export/{jobId:guid}");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.AuditView);
        Summary(summary =>
        {
            summary.Summary = "Get an audit-log export job status";
            summary.Description = "Returns the status of an async export. When completed and within the 24h window, the file is downloadable via the download endpoint.";
        });
        Tags("Audit/AuditLogs");
    }

    public override async Task HandleAsync(GetAuditLogExportRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;

        var job = await domainReadContext.AuditExportJobs
            .FirstOrDefaultAsync(j => j.Id == req.JobId && j.OrganizationId == organizationId, ct);

        if (job is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(new GetAuditLogExportResponse(
            job.Id,
            job.Status.ToString(),
            job.RowCount,
            job.CreatedAt,
            job.ExpiresAt,
            job.IsDownloadable(clock.GetCurrentInstant())),
            ct);
    }
}
