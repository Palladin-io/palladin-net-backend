using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Palladin.Core.Hangfire.CronJobs;
using Palladin.Module.Identity.Infrastructure.Persistence;

namespace Palladin.Module.Identity.Features;

[UsedImplicitly]
internal sealed class CleanupLoginRateLimitBucketsJobOptions : ICronJobOptions
{
    public const string Position = "Modules:Identity:CleanupLoginRateLimitBucketsJob";

    public bool Enabled { get; init; } = true;
    public string Expression { get; init; } = "*/5 * * * *";
    public int RetentionMinutes { get; init; } = 15;
    public int BatchSize { get; init; } = 500;
}

/// <summary>
/// Removes expired, opaque login-rate-limit partitions in bounded transactions. The job never
/// reads or logs the partition values; they remain HMAC digests throughout their lifetime.
/// </summary>
[UsedImplicitly]
internal sealed class CleanupLoginRateLimitBucketsJob(
    IdentityDomainWriteContext domainWriteContext,
    IOptions<CleanupLoginRateLimitBucketsJobOptions> options,
    IClock clock) : ICronJob
{
    public string Name => "identity.cleanup-login-rate-limit-buckets";
    public string Expression => options.Value.Expression;
    public bool Enabled => options.Value.Enabled;

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var cleanupOptions = options.Value;
        var cutoff = clock.GetCurrentInstant() - Duration.FromMinutes(cleanupOptions.RetentionMinutes);

        while (true)
        {
            var expired = await domainWriteContext.LoginRateLimitBuckets
                .Where(bucket => bucket.UpdatedAt < cutoff)
                .OrderBy(bucket => bucket.UpdatedAt)
                .ThenBy(bucket => bucket.Id)
                .Take(cleanupOptions.BatchSize)
                .ToListAsync(cancellationToken);
            if (expired.Count == 0)
            {
                return;
            }

            domainWriteContext.RemoveRange(expired);
            try
            {
                await domainWriteContext.CommitAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // A limiter refreshed one of these buckets after it was selected. Drop the stale
                // tracked snapshot and re-evaluate it against the cutoff without affecting auth.
            }
            finally
            {
                domainWriteContext.Clear();
            }

            if (expired.Count < cleanupOptions.BatchSize)
            {
                return;
            }
        }
    }
}
