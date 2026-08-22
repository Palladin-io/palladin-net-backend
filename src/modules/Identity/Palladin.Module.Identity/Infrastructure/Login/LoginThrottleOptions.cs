using JetBrains.Annotations;

namespace Palladin.Module.Identity.Infrastructure.Login;

// Leaf Position; the "Modules:Identity" prefix is composed at registration.
[UsedImplicitly]
internal sealed class LoginThrottleOptions
{
    public const string Position = "LoginThrottle";

    public int MaxAttempts { get; init; } = 4;
    public int WindowMinutes { get; init; } = 5;
    public int LockoutMinutes { get; init; } = 15;
    public int RateLimitWindowSeconds { get; init; } = 60;
    public int LoginIpPermitLimit { get; init; } = 30;
    public int LoginAccountPermitLimit { get; init; } = 10;
    public int TotpIpPermitLimit { get; init; } = 30;
    public int TotpAccountPermitLimit { get; init; } = 10;
    public int ConcurrencyRetryLimit { get; init; } = 64;
}
