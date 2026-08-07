using JetBrains.Annotations;

namespace Palladin.Module.Identity.Infrastructure.Login;

// Leaf Position; the "Modules:Identity" prefix is composed at registration.
[UsedImplicitly]
internal sealed class EmailVerificationOptions
{
    public const string Position = "EmailVerification";

    public int TokenTtlMinutes { get; init; } = 60;
    public int ResendCooldownMinutes { get; init; } = 2;

    // Web-panel route the emailed link points at; it POSTs the token to verify-email.
    public string VerificationUrlBase { get; init; } = string.Empty;

    // Hard gate on sensitive endpoints: unverified password users get 403. Default on; set false to
    // disable the gate via config (no redeploy) if email delivery is degraded.
    public bool GateEnabled { get; init; } = true;
}
