using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NodaTime;
using Palladin.Core.Guid;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.Persistence;

namespace Palladin.Module.Identity.Infrastructure.Login;

[UsedImplicitly]
internal sealed class LoginThrottleService(
    IServiceScopeFactory scopeFactory,
    IGuidProvider guidProvider,
    IOptions<LoginThrottleOptions> options) : ILoginThrottleService
{
    public async Task<LoginThrottleResult> GetStatusAsync(
        string normalizedEmail,
        string ipAddress,
        Instant now,
        CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<IdentityDomainReadContext>();
        var lockedUntil = await readContext.LoginLockouts
            .Where(x => x.Email == normalizedEmail && x.IpAddress == ipAddress)
            .Select(x => x.LockedUntil)
            .FirstOrDefaultAsync(ct);

        return lockedUntil is { } until && now < until
            ? LoginThrottleResult.Locked(until, now)
            : LoginThrottleResult.Available();
    }

    public async Task<LoginThrottleResult> RecordFailureAsync(
        string normalizedEmail,
        string ipAddress,
        Instant now,
        CancellationToken ct)
    {
        var throttleOptions = options.Value;
        for (var attempt = 0; attempt < throttleOptions.ConcurrencyRetryLimit; attempt++)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var writeContext = scope.ServiceProvider.GetRequiredService<IdentityDomainWriteContext>();
            var lockout = await writeContext.LoginLockouts
                .FirstOrDefaultAsync(x => x.Email == normalizedEmail && x.IpAddress == ipAddress, ct);

            if (lockout is null)
            {
                lockout = LoginLockout.Create(guidProvider.Generate(), normalizedEmail, ipAddress, now);
                writeContext.Add(lockout);
            }
            else if (lockout.IsLocked(now))
            {
                return LoginThrottleResult.Locked(lockout.LockedUntil!.Value, now);
            }

            var decision = lockout.RecordFailure(
                throttleOptions.MaxAttempts,
                Duration.FromMinutes(throttleOptions.WindowMinutes),
                Duration.FromMinutes(throttleOptions.LockoutMinutes),
                now);

            try
            {
                await writeContext.CommitAsync(ct);
                return decision.IsLocked
                    ? LoginThrottleResult.Locked(decision.LockedUntil!.Value, now)
                    : LoginThrottleResult.Available();
            }
            catch (Exception exception) when (LoginProtectionConcurrency.IsRetryable(exception))
            {
                await Task.Yield();
            }
        }

        return LoginThrottleResult.FailClosed();
    }

    public async Task<LoginThrottleResult> StageResetAsync(
        IdentityDomainWriteContext domainWriteContext,
        string normalizedEmail,
        string ipAddress,
        Instant now,
        CancellationToken ct)
    {
        var lockout = await domainWriteContext.LoginLockouts
            .FirstOrDefaultAsync(x => x.Email == normalizedEmail && x.IpAddress == ipAddress, ct);

        if (lockout is null)
        {
            return LoginThrottleResult.Available();
        }

        if (lockout.IsLocked(now))
        {
            return LoginThrottleResult.Locked(lockout.LockedUntil!.Value, now);
        }

        lockout.Reset(now);
        return LoginThrottleResult.Available();
    }
}
