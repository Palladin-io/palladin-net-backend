using Palladin.Module.Audit.Infrastructure.Exports;
using Palladin.Core.Security;
using Palladin.Module.Audit.Domain;
using Palladin.Module.Audit.Infrastructure.Persistence;
using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Palladin.Module.Audit.Features.Export;

[PublicAPI]
public sealed record DownloadAuditLogExportRequest
{
    public Guid JobId { get; init; }
}

[PublicAPI]
public sealed record DownloadAuditLogExportResponse(string DownloadUrl);

[PublicAPI]
internal sealed class DownloadAuditLogExportEndpoint(
    AuditDomainReadContext domainReadContext,
    IAuditExportStorage exportStorage,
    IClock clock) : Endpoint<DownloadAuditLogExportRequest, DownloadAuditLogExportResponse>
{
    public override void Configure()
    {
        Get("api/audit-logs/export/{jobId:guid}/download");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.AuditView);
        Summary(summary =>
        {
            summary.Summary = "Get a time-boxed download link for an audit-log export";
            summary.Description = "Returns a presigned URL for the generated CSV. Valid only while the export is within its 24h window; returns 410 once expired.";
        });
        Tags("Audit/AuditLogs");
    }

    public override async Task HandleAsync(DownloadAuditLogExportRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;

        var job = await domainReadContext.AuditExportJobs
            .FirstOrDefaultAsync(j => j.Id == req.JobId && j.OrganizationId == organizationId, ct);

        if (job is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (job.Status != AuditExportStatus.Completed || job.StorageKey is null)
        {
            await Send.ResultAsync(Results.StatusCode(StatusCodes.Status409Conflict));
            return;
        }

        if (!job.IsDownloadable(clock.GetCurrentInstant()))
        {
            await Send.ResultAsync(Results.StatusCode(StatusCodes.Status410Gone));
            return;
        }

        var url = await exportStorage.CreateDownloadUrlAsync(job.StorageKey, ct);
        await Send.OkAsync(new DownloadAuditLogExportResponse(url), ct);
    }
}
