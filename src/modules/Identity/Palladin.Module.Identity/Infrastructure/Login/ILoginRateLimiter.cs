using NodaTime;

namespace Palladin.Module.Identity.Infrastructure.Login;

internal interface ILoginRateLimiter
{
    Task<LoginRateLimitLease> AcquireLoginAsync(
        string normalizedEmail,
        string ipAddress,
        Instant now,
        CancellationToken ct);

    Task<LoginRateLimitLease> AcquireTotpIpAsync(
        string ipAddress,
        Instant now,
        CancellationToken ct);

    Task<LoginRateLimitLease> AcquireTotpAccountAsync(
        string normalizedEmail,
        Instant now,
        CancellationToken ct);
}

internal readonly record struct LoginRateLimitLease(bool IsAcquired, int RetryAfterSeconds)
{
    internal static LoginRateLimitLease Acquired() => new(true, 0);

    internal static LoginRateLimitLease Rejected(Instant windowEndsAt, Instant now) =>
        new(false, Math.Max(1, (int)Math.Ceiling((windowEndsAt - now).TotalSeconds)));

    internal static LoginRateLimitLease FailClosed() => new(false, 1);
}
