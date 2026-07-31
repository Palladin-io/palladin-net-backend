namespace Palladin.Core.Hangfire.CronJobs;

public interface ICronJob
{
    string Name { get; }
    string Expression { get; }
    bool Enabled { get; }

    Task ExecuteAsync(CancellationToken cancellationToken = default);
}

public interface ICronJobOptions
{
    bool Enabled { get; }
}
