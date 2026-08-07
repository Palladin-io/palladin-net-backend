using Palladin.Core.Hangfire.CronJobs;
using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Persistence;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Palladin.Module.Vault.Features;

[UsedImplicitly]
internal sealed class ExpireGrantsJobOptions : ICronJobOptions
{
    public const string Position = "Modules:Vault:ExpireGrantsJob";

    public bool Enabled { get; init; } = true;
    public string Expression { get; init; } = "*/5 * * * *";
    public int BatchSize { get; init; } = 100;
}

[UsedImplicitly]
internal sealed class ExpireGrantsJob(
    VaultDomainWriteContext domainWriteContext,
    IOptions<ExpireGrantsJobOptions> options,
    IClock clock,
    ILogger<ExpireGrantsJob> logger) : ICronJob
{
    public string Name => "vault.expire-grants";
    public string Expression => options.Value.Expression;
    public bool Enabled => options.Value.Enabled;

    // Transitions time-based grants (ExpiresAt set, in the past) from Active to Expired and emits a
    // GrantExpired event per grant. Use-based grants (QueryLimit set, ExpiresAt null) and Lifetime
    // grants (both null — never expires) are not time-based and are left untouched;
    // Consumed/Revoked/Denied are not Active. Idempotent: only Active grants with a past ExpiresAt
    // are selected.
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetCurrentInstant();
        var batchSize = options.Value.BatchSize;
        var totalExpired = 0;

        while (true)
        {
            var dueGrants = await domainWriteContext.Grants
                .Include(g => g.GrantEntryScopes).ThenInclude(scope => scope.Envelope)
                .Where(g => g.Status == GrantStatus.Active
                            && g.ExpiresAt != null
                            && g.ExpiresAt <= now)
                .OrderBy(g => g.ExpiresAt)
                .Take(batchSize)
                .ToListAsync(cancellationToken);

            if (dueGrants.Count == 0)
            {
                break;
            }

            // Bulk-load entry labels for the GRANULAR grants in this batch so the GrantExpired event
            // can carry the denormalized label per CLAUDE.md write-time denormalization. One IN-set
            // query per batch — FULL grants have no specific entry, so they pass null.
            var granularEntryIds = dueGrants
                .OfType<GranularGrant>()
                .Select(g => g.EntryId)
                .Distinct()
                .ToArray();
            var entryLabels = granularEntryIds.ToDictionary(x => x, _ => (string?)null);

            foreach (var grant in dueGrants)
            {
                var entryLabel = grant is GranularGrant g
                    ? entryLabels.GetValueOrDefault(g.EntryId)
                    : null;
                grant.Expire(now, entryLabel);
            }

            await domainWriteContext.CommitAsync(cancellationToken);

            // Keep the change tracker from growing across batches on large expiry runs. Safe here:
            // events were already dispatched by CommitAsync, so nothing tracked is still needed.
            domainWriteContext.Clear();

            totalExpired += dueGrants.Count;

            if (dueGrants.Count < batchSize)
            {
                break;
            }
        }

        if (totalExpired > 0)
        {
            logger.LogInformation("Expired {ExpiredGrantCount} grant(s)", totalExpired);
        }
    }
}
