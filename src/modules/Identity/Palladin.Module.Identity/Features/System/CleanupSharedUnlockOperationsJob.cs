using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Palladin.Core.Hangfire.CronJobs;
using Palladin.Module.Identity.Infrastructure.Persistence;

namespace Palladin.Module.Identity.Features;

[UsedImplicitly]
internal sealed class CleanupSharedUnlockOperationsJobOptions : ICronJobOptions
{
    public const string Position = "Modules:Identity:CleanupSharedUnlockOperationsJob";
    public bool Enabled { get; init; } = true;
    public string Expression { get; init; } = "*/5 * * * *";
    public int BatchSize { get; init; } = 500;
}

[UsedImplicitly]
internal sealed class CleanupSharedUnlockOperationsJob(IdentityDomainWriteContext context,
    IOptions<CleanupSharedUnlockOperationsJobOptions> options, IClock clock) : ICronJob
{
    public string Name => "identity.cleanup-shared-unlock-operations";
    public bool Enabled => options.Value.Enabled;
    public string Expression => options.Value.Expression;

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = clock.GetCurrentInstant();
        var batchSize = options.Value.BatchSize;
        while (true)
        {
            var expired = await context.SharedUnlockOperations.Where(operation => operation.ExpiresAt <= cutoff)
                .OrderBy(operation => operation.ExpiresAt).ThenBy(operation => operation.Id)
                .Take(batchSize).ToListAsync(cancellationToken);
            if (expired.Count == 0)
            {
                return;
            }
            context.RemoveRange(expired);
            try
            {
                await context.CommitAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
            }
            finally
            {
                context.Clear();
            }
            if (expired.Count < batchSize)
            {
                return;
            }
        }
    }
}
