using Hangfire;
using Hangfire.Storage;

namespace Palladin.Core.Hangfire.CronJobs;

internal interface IJobStorage
{
    IStorageConnection GetCurrentConnection();
}

internal sealed class JobStorageWrapper : IJobStorage
{
    public IStorageConnection GetCurrentConnection() => JobStorage.Current.GetConnection();
}
