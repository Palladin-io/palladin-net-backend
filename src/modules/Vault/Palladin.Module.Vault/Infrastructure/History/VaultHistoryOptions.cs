using Palladin.Core.Hangfire.CronJobs;

namespace Palladin.Module.Vault.Infrastructure.History;

internal sealed class VaultHistoryOptions
{
    public const string Position = "Modules:Vault:History";

    public int MaximumVersions { get; init; } = 100;
    public int MaximumAgeDays { get; init; } = 365;
    public int DefaultPageSize { get; init; } = 20;
    public int MaximumPageSize { get; init; } = 100;
}

internal sealed class VaultEntryLifecycleOptions : ICronJobOptions
{
    public const string Position = "Modules:Vault:EntryLifecycle";

    public int RecentlyDeletedDays { get; init; } = 30;
    public bool Enabled { get; init; } = true;
    public string Expression { get; init; } = "17 */6 * * *";
    public int BatchSize { get; init; } = 100;
}
