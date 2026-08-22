using NodaTime;
using Palladin.Module.Identity.Infrastructure.Persistence;

namespace Palladin.Module.Identity.Infrastructure.Login;

internal interface ILoginThrottleService
{
    Task<LoginThrottleResult> GetStatusAsync(
        string normalizedEmail,
        string ipAddress,
        Instant now,
        CancellationToken ct);

    Task<LoginThrottleResult> RecordFailureAsync(
        string normalizedEmail,
        string ipAddress,
        Instant now,
        CancellationToken ct);

    Task<LoginThrottleResult> StageResetAsync(
        IdentityDomainWriteContext domainWriteContext,
        string normalizedEmail,
        string ipAddress,
        Instant now,
        CancellationToken ct);
}

internal readonly record struct LoginThrottleResult(bool IsLocked, int RetryAfterSeconds)
{
    internal static LoginThrottleResult Available() => new(false, 0);

    internal static LoginThrottleResult Locked(Instant lockedUntil, Instant now) =>
        new(true, Math.Max(1, (int)Math.Ceiling((lockedUntil - now).TotalSeconds)));

    internal static LoginThrottleResult FailClosed() => new(true, 1);
}
