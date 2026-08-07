using Palladin.Core.Guid;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.Persistence;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Palladin.Module.Identity.Infrastructure.Login;

[UsedImplicitly]
internal sealed class LoginThrottleService(
    IdentityDbWriteContext writeContext,
    IdentityDomainWriteContext domainWriteContext,
    IGuidProvider guidProvider,
    IOptions<LoginThrottleOptions> options) : ILoginThrottleService
{
    public async Task<bool> IsLockedAsync(string normalizedEmail, string ipAddress, Instant now, CancellationToken ct)
    {
        var lockedUntil = await writeContext.LoginLockouts
            .Where(x => x.Email == normalizedEmail && x.IpAddress == ipAddress)
            .Select(x => x.LockedUntil)
            .FirstOrDefaultAsync(ct);

        return lockedUntil is { } until && now < until;
    }

    public async Task RecordFailureAsync(string normalizedEmail, string ipAddress, Instant now, CancellationToken ct)
    {
        var opts = options.Value;
        var lockout = await writeContext.LoginLockouts
            .FirstOrDefaultAsync(x => x.Email == normalizedEmail && x.IpAddress == ipAddress, ct);

        if (lockout is null)
        {
            lockout = LoginLockout.Create(guidProvider.Generate(), normalizedEmail, ipAddress, now);
            domainWriteContext.Add(lockout);
        }

        lockout.RecordFailure(
            opts.MaxAttempts,
            Duration.FromMinutes(opts.WindowMinutes),
            Duration.FromMinutes(opts.LockoutMinutes),
            now);

        await domainWriteContext.CommitAsync(ct);
    }

    // Mutates the tracked lockout only; the caller's CommitAsync persists it in one unit of work.
    public async Task ResetAsync(string normalizedEmail, string ipAddress, Instant now, CancellationToken ct)
    {
        var lockout = await writeContext.LoginLockouts
            .FirstOrDefaultAsync(x => x.Email == normalizedEmail && x.IpAddress == ipAddress, ct);

        lockout?.Reset(now);
    }
}
