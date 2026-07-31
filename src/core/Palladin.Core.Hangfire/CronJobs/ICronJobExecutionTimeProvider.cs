namespace Palladin.Core.Hangfire.CronJobs;

public interface ICronJobExecutionTimeProvider
{
    Task<DateTime?> GetLastSuccessfulExecutionStartAsync(string jobName, CancellationToken cancellationToken = default);
}
