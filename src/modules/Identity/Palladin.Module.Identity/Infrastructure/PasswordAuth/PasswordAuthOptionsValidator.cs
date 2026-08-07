using JetBrains.Annotations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Palladin.Module.Identity.Infrastructure.PasswordAuth;

// Fail-fast: an empty EnumerationSecret outside Development silently disables anti-enumeration —
// HMAC("", email) is still deterministic, but the key would be public/guessable. Refuse to start.
[UsedImplicitly]
internal sealed class PasswordAuthOptionsValidator(IHostEnvironment environment) : IValidateOptions<PasswordAuthOptions>
{
    public ValidateOptionsResult Validate(string? name, PasswordAuthOptions options)
    {
        if (environment.IsDevelopment() || !string.IsNullOrWhiteSpace(options.EnumerationSecret))
        {
            return ValidateOptionsResult.Success;
        }

        return ValidateOptionsResult.Fail(
            "Modules:Identity:PasswordAuth:EnumerationSecret must be set outside Development "
            + "(HMAC key for anti-enumeration login/salt; a separate secret from the JWT secret).");
    }
}
