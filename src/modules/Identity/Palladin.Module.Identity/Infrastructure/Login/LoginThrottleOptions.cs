using JetBrains.Annotations;

namespace Palladin.Module.Identity.Infrastructure.Login;

// Leaf Position; the "Modules:Identity" prefix is composed at registration.
[UsedImplicitly]
internal sealed class LoginThrottleOptions
{
    public const string Position = "LoginThrottle";

    public int MaxAttempts { get; init; } = 5;
    public int WindowMinutes { get; init; } = 15;
    public int LockoutMinutes { get; init; } = 15;
}
