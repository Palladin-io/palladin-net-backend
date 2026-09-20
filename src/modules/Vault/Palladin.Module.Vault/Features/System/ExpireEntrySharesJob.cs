using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Palladin.Core.Hangfire.CronJobs;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Features;

[UsedImplicitly]
internal sealed class ExpireEntrySharesJobOptions : ICronJobOptions
{
    public const string Position = "Modules:Vault:ExpireEntrySharesJob";
    public bool Enabled { get; init; } = true;
    public string Expression { get; init; } = "* * * * *";
    public int BatchSize { get; init; } = 100;
    public int MaximumBatches { get; init; } = 100;
}

[UsedImplicitly]
internal sealed class ExpireEntrySharesJob(
    VaultDomainWriteContext domainWriteContext,
    IOptions<ExpireEntrySharesJobOptions> options,
    IClock clock) : ICronJob
{
    public string Name => "vault.expire-entry-shares";
    public string Expression => options.Value.Expression;
    public bool Enabled => options.Value.Enabled;

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetCurrentInstant();
        var batchSize = Math.Clamp(options.Value.BatchSize, 1, 200);
        for (var batch = 0; batch < Math.Clamp(options.Value.MaximumBatches, 1, 100); batch++)
        {
            var shares = await domainWriteContext.EntryShares
                .Where(x => x.RevokedAt == null && x.ExpiredAt == null && x.ExpiresAt <= now)
                .OrderBy(x => x.ExpiresAt).ThenBy(x => x.Id)
                .Take(batchSize).ToListAsync(cancellationToken);
            if (shares.Count == 0)
            {
                break;
            }

            foreach (var share in shares)
            {
                share.Expire(now);
            }

            await domainWriteContext.CommitAsync(cancellationToken);
            domainWriteContext.Clear();
        }

        for (var batch = 0; batch < Math.Clamp(options.Value.MaximumBatches, 1, 100); batch++)
        {
            var sessions = await domainWriteContext.EntryShareSessions
                .Where(x => x.ExpiresAt <= now)
                .OrderBy(x => x.ExpiresAt).ThenBy(x => x.ShareId).ThenBy(x => x.Id)
                .Take(batchSize).ToListAsync(cancellationToken);
            if (sessions.Count == 0)
            {
                break;
            }

            domainWriteContext.RemoveRange(sessions);
            await domainWriteContext.CommitAsync(cancellationToken);
            domainWriteContext.Clear();
        }
    }
}
