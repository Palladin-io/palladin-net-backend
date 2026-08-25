using JetBrains.Annotations;

namespace Palladin.Module.Identity.Infrastructure.Waitlist;

[UsedImplicitly]
internal sealed class WaitlistOptions
{
    public const string Position = "Waitlist";

    public bool Enabled { get; init; }
    public bool BenefitEnabled { get; init; }

    public int TokenTtlHours { get; init; } = 24;
    public int ResendCooldownMinutes { get; init; } = 5;
    public int BenefitDurationMonths { get; init; } = 1;
    public DateTimeOffset? PublicLaunchAtUtc { get; init; }
    public DateTimeOffset? BenefitClaimDeadlineAtUtc { get; init; }
    public string PromotionTermsVersion { get; init; } = string.Empty;

    public string VerificationUrlBase { get; init; } = string.Empty;
    public string VerifiedRedirectUrl { get; init; } = string.Empty;
    public string AlreadyVerifiedRedirectUrl { get; init; } = string.Empty;
    public string InvalidRedirectUrl { get; init; } = string.Empty;
    public string TemporaryFailureRedirectUrl { get; init; } = string.Empty;
}
