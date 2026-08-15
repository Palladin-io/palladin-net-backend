using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using Palladin.Core.Hangfire.CronJobs;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Features;

[UsedImplicitly]
internal sealed class CleanupFullGrantPreparationsJobOptions : ICronJobOptions
{
    public const string Position = "Modules:Vault:CleanupFullGrantPreparationsJob";

    public bool Enabled { get; init; } = true;
    public string Expression { get; init; } = "*/5 * * * *";
}

[UsedImplicitly]
internal sealed class CleanupFullGrantPreparationsJob(
    VaultDomainWriteContext domainWriteContext,
    IOptions<CleanupFullGrantPreparationsJobOptions> jobOptions,
    IOptions<VaultCryptoOptions> cryptoOptions,
    IClock clock,
    ILogger<CleanupFullGrantPreparationsJob> logger) : ICronJob
{
    public string Name => "vault.cleanup-full-grant-preparations";
    public string Expression => jobOptions.Value.Expression;
    public bool Enabled => jobOptions.Value.Enabled;

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var totalRemoved = 0;
        while (true)
        {
            var expired = await domainWriteContext.FullGrantPreparations
                .Where(x => x.PreparationExpiresAt <= clock.GetCurrentInstant())
                .OrderBy(x => x.PreparationExpiresAt)
                .ThenBy(x => x.Id)
                .Take(cryptoOptions.Value.FullGrantPreparationCleanupBatchSize)
                .ToListAsync(cancellationToken);
            if (expired.Count == 0)
            {
                break;
            }

            domainWriteContext.RemoveRange(expired);
            await domainWriteContext.CommitAsync(cancellationToken);
            domainWriteContext.Clear();
            totalRemoved += expired.Count;
            if (expired.Count < cryptoOptions.Value.FullGrantPreparationCleanupBatchSize)
            {
                break;
            }
        }

        if (totalRemoved > 0)
        {
            logger.LogInformation("Removed {ExpiredPreparationCount} expired FULL grant preparation(s)", totalRemoved);
        }
    }
}
