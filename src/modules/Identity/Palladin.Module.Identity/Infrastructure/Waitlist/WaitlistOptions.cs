using JetBrains.Annotations;

namespace Palladin.Module.Identity.Infrastructure.Waitlist;

[UsedImplicitly]
internal sealed class WaitlistOptions
{
    public const string Position = "Waitlist";

    // Kill switch for the whole waitlist surface — both endpoints return 404 when disabled.
    // Keep false until the configured email provider is approved for production sending.
    public bool Enabled { get; init; }

    public int TokenTtlHours { get; init; } = 24;
    public int ResendCooldownMinutes { get; init; } = 5;

    // Public GET endpoint of this API that the email link points at.
    public string VerificationUrlBase { get; init; } = string.Empty;

    // Landing-page destinations the verify endpoint redirects to.
    public string VerifiedRedirectUrl { get; init; } = string.Empty;
    public string FailedRedirectUrl { get; init; } = string.Empty;
}
