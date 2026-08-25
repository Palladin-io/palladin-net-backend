using JetBrains.Annotations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Palladin.Module.Identity.Domain;

namespace Palladin.Module.Identity.Infrastructure.Waitlist;

[UsedImplicitly]
internal sealed class WaitlistOptionsValidator(IHostEnvironment environment) : IValidateOptions<WaitlistOptions>
{
    public ValidateOptionsResult Validate(string? name, WaitlistOptions options)
    {
        var failures = new List<string>();

        if (options.Enabled || options.BenefitEnabled)
        {
            if (options.TokenTtlHours <= 0)
            {
                failures.Add("TokenTtlHours must be greater than zero.");
            }

            if (options.ResendCooldownMinutes < 0)
            {
                failures.Add("ResendCooldownMinutes cannot be negative.");
            }

            ValidateUrl(options.VerificationUrlBase, nameof(options.VerificationUrlBase), failures);
            ValidateUrl(options.VerifiedRedirectUrl, nameof(options.VerifiedRedirectUrl), failures);
            ValidateUrl(options.AlreadyVerifiedRedirectUrl, nameof(options.AlreadyVerifiedRedirectUrl), failures);
            ValidateUrl(options.InvalidRedirectUrl, nameof(options.InvalidRedirectUrl), failures);
            ValidateUrl(options.TemporaryFailureRedirectUrl, nameof(options.TemporaryFailureRedirectUrl), failures);
        }

        if (options.BenefitEnabled)
        {
            ValidateBenefit(options, failures);
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private void ValidateUrl(string value, string property, ICollection<string> failures)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            failures.Add($"{property} must be an absolute URL.");
            return;
        }

        if (!environment.IsDevelopment()
            && !environment.IsEnvironment("Testing")
            && uri.Scheme != Uri.UriSchemeHttps)
        {
            failures.Add($"{property} must use HTTPS outside Development and Testing.");
        }
    }

    private static void ValidateBenefit(WaitlistOptions options, ICollection<string> failures)
    {
        if (options.BenefitDurationMonths != 1)
        {
            failures.Add("BenefitDurationMonths must equal one.");
        }

        if (options.PromotionTermsVersion != WaitlistEntry.CurrentPromotionTermsVersion)
        {
            failures.Add($"PromotionTermsVersion must equal {WaitlistEntry.CurrentPromotionTermsVersion}.");
        }

        if (options.PublicLaunchAtUtc is null)
        {
            failures.Add("PublicLaunchAtUtc is required when the waitlist benefit is enabled.");
        }

        if (options.BenefitClaimDeadlineAtUtc is null)
        {
            failures.Add("BenefitClaimDeadlineAtUtc is required when the waitlist benefit is enabled.");
        }

        if (options.PublicLaunchAtUtc is not { } publicLaunch
            || options.BenefitClaimDeadlineAtUtc is not { } claimDeadline)
        {
            return;
        }

        if (publicLaunch.Offset != TimeSpan.Zero || claimDeadline.Offset != TimeSpan.Zero)
        {
            failures.Add("Waitlist benefit dates must use UTC.");
        }

        if (claimDeadline != publicLaunch.AddMonths(6))
        {
            failures.Add("BenefitClaimDeadlineAtUtc must be exactly six calendar months after PublicLaunchAtUtc.");
        }
    }
}
