using System.Threading.RateLimiting;

namespace Palladin.Module.PublicAssetCatalog.Infrastructure.Acquisition;

/// <summary>Charges authenticated Members for every submitted hostname, not merely every HTTP call.</summary>
internal sealed class WebsiteIconEnsureLimiter : IDisposable
{
    private const int HostnamesPerMinute = 500;
    private readonly PartitionedRateLimiter<(Guid MemberId, int Permits)> limiter =
        PartitionedRateLimiter.Create<(Guid MemberId, int Permits), Guid>(request =>
            RateLimitPartition.GetFixedWindowLimiter(request.MemberId, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = HostnamesPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

    internal bool TryAcquire(Guid memberId, int hostnameCount)
    {
        if (hostnameCount is < 1 or > HostnamesPerMinute) return false;
        using var lease = limiter.AttemptAcquire((memberId, hostnameCount), hostnameCount);
        return lease.IsAcquired;
    }

    public void Dispose() => limiter.Dispose();
}
