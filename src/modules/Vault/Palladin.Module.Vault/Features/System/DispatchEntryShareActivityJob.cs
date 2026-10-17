using JetBrains.Annotations;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Palladin.Core.Hangfire.CronJobs;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Features;

[UsedImplicitly]
internal sealed class DispatchEntryShareActivityJobOptions : ICronJobOptions
{
    public const string Position = "Modules:Vault:DispatchEntryShareActivityJob";
    public bool Enabled { get; init; } = true;
    public string Expression { get; init; } = "* * * * *";
    public int BatchSize { get; init; } = 100;
    public int MaximumBatches { get; init; } = 10;
}

[UsedImplicitly]
internal sealed class DispatchEntryShareActivityJob(
    VaultDomainWriteContext domainWriteContext,
    IOptions<DispatchEntryShareActivityJobOptions> options,
    IPublishEndpoint publishEndpoint,
    IClock clock) : ICronJob
{
    public string Name => "vault.dispatch-entry-share-activity";
    public string Expression => options.Value.Expression;
    public bool Enabled => options.Value.Enabled;

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var batchSize = Math.Clamp(options.Value.BatchSize, 1, 200);
        for (var batch = 0; batch < Math.Clamp(options.Value.MaximumBatches, 1, 100); batch++)
        {
            var activities = await domainWriteContext.EntryShareActivities
                .Where(x => x.PublishedAt == null)
                .OrderBy(x => x.OccurredAt).ThenBy(x => x.ShareId).ThenBy(x => x.Sequence)
                .Take(batchSize).ToListAsync(cancellationToken);
            if (activities.Count == 0)
            {
                return;
            }

            foreach (var activity in activities)
            {
                await publishEndpoint.Publish(activity.ToEvent(), cancellationToken);
                activity.MarkPublished(clock.GetCurrentInstant());
            }

            await domainWriteContext.CommitAsync(cancellationToken);
            domainWriteContext.Clear();
        }
    }
}
