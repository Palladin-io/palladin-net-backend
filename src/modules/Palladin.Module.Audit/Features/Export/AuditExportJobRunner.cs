using System.Text;
using System.Text.Json;
using Palladin.Module.Audit.Infrastructure.Exports;
using Palladin.Module.Audit.Domain;
using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;
using Palladin.Module.Audit.Infrastructure.Persistence;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ILogger = Serilog.ILogger;

namespace Palladin.Module.Audit.Features.Export;

internal static class AuditExportStorage
{
    internal const string Prefix = "audit-exports/";
}

internal interface IAuditExportJobRunner
{
    Task RunAsync(Guid jobId, CancellationToken ct);
}

// Hangfire-driven generation of an audit-log CSV. Rows are fetched in keyset-paged batches (no full
// materialization, no tracking) and streamed into the CSV, which is uploaded to object storage. The
// download stays available for DownloadWindow; the export NEVER contains secrets/ciphertext — audit
// rows only ever hold non-sensitive metadata.
[UsedImplicitly]
internal sealed class AuditExportJobRunner(
    AuditDomainReadContext domainReadContext,
    AuditDomainWriteContext domainWriteContext,
    IAuditExportStorage exportStorage,
    IClock clock,
    ILogger logger) : IAuditExportJobRunner
{
    private const int BatchSize = 500;
    private static readonly Duration DownloadWindow = Duration.FromHours(24);

    public async Task RunAsync(Guid jobId, CancellationToken ct)
    {
        var job = await domainWriteContext.AuditExportJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null || job.Status != AuditExportStatus.Pending)
        {
            return;
        }

        var tempPath = Path.GetTempFileName();
        try
        {
            var storageKey = $"{AuditExportStorage.Prefix}{job.OrganizationId}/{job.Id}.csv";

            int rowCount;
            await using (var writeStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            await using (var csvWriter = new StreamWriter(writeStream, new UTF8Encoding(false)))
            {
                rowCount = await WriteCsvAsync(csvWriter, job, ct);
            }

            await using (var uploadStream = new FileStream(tempPath, FileMode.Open, FileAccess.Read))
            {
                await exportStorage.UploadAsync(uploadStream, storageKey, ct);
            }

            job.MarkCompleted(storageKey, rowCount, clock.GetCurrentInstant(), DownloadWindow);
            await domainWriteContext.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            logger.Error(
                "Audit export {JobId} failed with {ErrorType}",
                jobId,
                ex.GetType().Name);
            job.MarkFailed("export_failed", clock.GetCurrentInstant());
            await domainWriteContext.CommitAsync(ct);
            throw;
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    private async Task<int> WriteCsvAsync(TextWriter writer, AuditExportJob job, CancellationToken ct)
    {
        await AuditCsv.WriteRowAsync(writer, AuditCsv.Header);

        var baseQuery = AuditLogQuery.ApplyFilters(
            domainReadContext.AuditLogEntries
                .AsNoTracking()
                .Where(e => e.OrganizationId == job.OrganizationId),
            AuditFilterParsing.ParseGuids(job.VaultIds),
            AuditFilterParsing.ParseGuids(job.AgentIds),
            AuditFilterParsing.ParseGuids(job.UserIds),
            job.EntryId,
            AuditFilterParsing.ParseStrings(job.EventTypes),
            job.From, job.To);

        var rowCount = 0;
        var cursorTimestamp = Instant.MinValue;
        var cursorId = Guid.Empty;

        while (true)
        {
            var batch = await baseQuery
                .Where(e => e.CreatedAt > cursorTimestamp
                            || (e.CreatedAt == cursorTimestamp && e.Id.CompareTo(cursorId) > 0))
                .OrderBy(e => e.CreatedAt)
                .ThenBy(e => e.Id)
                .Take(BatchSize)
                .Select(e => new AuditExportRow(
                    e.Id, e.CreatedAt, e.OccurredAt, e.EventType, e.ActorType, e.Result,
                    e.ActorName, e.AgentName, e.UserId, e.AgentId, e.VaultId, e.EntryId, e.Metadata))
                .ToListAsync(ct);

            if (batch.Count == 0)
            {
                break;
            }

            foreach (var row in batch)
            {
                await AuditCsv.WriteRowAsync(writer,
                [
                    row.OccurredAt.ToString(),
                    row.EventType,
                    row.ActorType.ToString(),
                    row.Result.ToString(),
                    row.ActorName,
                    row.AgentName,
                    row.UserId?.ToString(),
                    row.AgentId?.ToString(),
                    row.VaultId?.ToString(),
                    row.EntryId?.ToString(),
                    JsonSerializer.Serialize(row.Metadata),
                ]);
                rowCount++;
            }

            var last = batch[^1];
            cursorTimestamp = last.CreatedAt;
            cursorId = last.Id;

            if (batch.Count < BatchSize)
            {
                break;
            }
        }

        return rowCount;
    }

    private sealed record AuditExportRow(
        Guid Id,
        Instant CreatedAt,
        Instant OccurredAt,
        string EventType,
        AuditActorType ActorType,
        AuditResult Result,
        string? ActorName,
        string? AgentName,
        Guid? UserId,
        Guid? AgentId,
        Guid? VaultId,
        Guid? EntryId,
        IReadOnlyDictionary<string, string> Metadata);
}
