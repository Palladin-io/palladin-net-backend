using JetBrains.Annotations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Palladin.Module.Identity.Infrastructure.Waitlist;

[UsedImplicitly]
internal sealed class WaitlistOptionsValidator(IHostEnvironment environment) : IValidateOptions<WaitlistOptions>
{
    public ValidateOptionsResult Validate(string? name, WaitlistOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

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
        ValidateUrl(options.FailedRedirectUrl, nameof(options.FailedRedirectUrl), failures);

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
}
