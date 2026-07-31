using Hangfire;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Palladin.Core.Hangfire.CronJobs;

internal sealed class HangfireCronJobsInitializer(
    IServiceProvider serviceProvider,
    IOptions<HangfireOptions> hangfireOptions,
    ILogger<HangfireCronJobsInitializer> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var jobStorage = serviceProvider.GetRequiredService<IJobStorage>();
        var recurringJobManager = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();
        var cronJobs = scope.ServiceProvider.GetServices<ICronJob>().ToArray();
        ClearOutdatedCronJobs(jobStorage, recurringJobManager, cronJobs);
        AddCronJobs(recurringJobManager, cronJobs);
    }

    private void ClearOutdatedCronJobs(
        IJobStorage jobStorage,
        IRecurringJobManager recurringJobManager,
        IReadOnlyCollection<ICronJob> cronJobs
    )
    {
        using var connection = jobStorage.GetCurrentConnection();
        var recurringJobs = connection.GetRecurringJobs();
        foreach (var recurringJob in recurringJobs)
        {
            if (hangfireOptions.Value.Enabled! || cronJobs.All(x => x.Name != recurringJob.Id))
            {
                recurringJobManager.RemoveIfExists(recurringJob.Id);
            }
        }
    }

    private void AddCronJobs(IRecurringJobManager recurringJobManager, IEnumerable<ICronJob> cronJobs)
    {
        if (!hangfireOptions.Value.Enabled)
        {
            return;
        }

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(hangfireOptions.Value.TimeZoneId);
        var enabledCronJobs = cronJobs.Where(x => x.Enabled).ToArray();

        foreach (var cronJob in enabledCronJobs)
        {
            logger.LogInformation(
                "Add cron job {CronJobName} with expression {CronJobExpression}",
                cronJob.Name,
                cronJob.Expression
            );

            recurringJobManager.AddOrUpdate(
                cronJob.Name,
                () => cronJob.ExecuteAsync(CancellationToken.None),
                cronJob.Expression,
                new RecurringJobOptions { TimeZone = timeZone }
            );
        }
    }
}
