using JetBrains.Annotations;

namespace Palladin.Module.Identity.Infrastructure.Totp;

// Leaf Position; the "Modules:Identity" prefix is composed at registration.
[UsedImplicitly]
internal sealed class TotpOptions
{
    public const string Position = "Totp";

    // Label shown in the authenticator app (otpauth issuer).
    public string Issuer { get; init; } = "Palladin";
    public int SecretSizeBytes { get; init; } = 20;
    public int RecoveryCodeCount { get; init; } = 10;

    // Number of ±30s steps accepted on either side of the current one.
    public int VerificationWindowSteps { get; init; } = 1;

    // TTL of the short-lived login challenge issued when TOTP is required.
    public int ChallengeTtlMinutes { get; init; } = 5;
}
