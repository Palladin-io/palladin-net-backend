using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using Palladin.Core.Hangfire.CronJobs;
using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Features;

[UsedImplicitly]
internal sealed class PurgeTrashedSubmissionsJobOptions : ICronJobOptions
{
    public const string Position = "Modules:Vault:PurgeTrashedSubmissionsJob";

    public bool Enabled { get; init; } = true;
    public string Expression { get; init; } = "17 * * * *";
    public int BatchSize { get; init; } = 100;
}

// Permanently deletes the ciphertext and wrapped DEKs of submissions trashed for the contract's 30 days.
// The structural row and its audit history remain; every page commits through the domain context.
[UsedImplicitly]
internal sealed class PurgeTrashedSubmissionsJob(
    VaultDomainWriteContext domainWriteContext,
    IOptions<PurgeTrashedSubmissionsJobOptions> options,
    IClock clock,
    ILogger<PurgeTrashedSubmissionsJob> logger) : ICronJob
{
    public string Name => "vault.purge-trashed-submissions";
    public string Expression => options.Value.Expression;
    public bool Enabled => options.Value.Enabled;

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetCurrentInstant();
        var cutoff = now - Duration.FromDays(AgentSubmissionLimits.TrashRetentionDays);
        var purged = 0;
        while (true)
        {
            var due = await domainWriteContext.AgentEntrySubmissions
                .Where(x => x.State == AgentSubmissionState.Trashed && x.TrashedAt <= cutoff)
                .OrderBy(x => x.TrashedAt)
                .ThenBy(x => x.OrganizationId)
                .ThenBy(x => x.Id)
                .Take(options.Value.BatchSize)
                .ToListAsync(cancellationToken);
            if (due.Count == 0)
            {
                break;
            }

            foreach (var submission in due)
            {
                submission.Purge(now);
            }

            await domainWriteContext.CommitAsync(cancellationToken);
            domainWriteContext.Clear();
            purged += due.Count;
        }

        if (purged > 0)
        {
            logger.LogInformation("Purged {Count} trashed Agent submissions", purged);
        }
    }
}
