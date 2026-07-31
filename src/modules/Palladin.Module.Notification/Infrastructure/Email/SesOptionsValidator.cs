using JetBrains.Annotations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Net.Mail;

namespace Palladin.Module.Notification.Infrastructure.Email;

[UsedImplicitly]
internal sealed class SesOptionsValidator(IHostEnvironment environment) : IValidateOptions<SesOptions>
{
    public ValidateOptionsResult Validate(string? name, SesOptions options)
    {
        if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
        {
            return ValidateOptionsResult.Success;
        }

        if (string.IsNullOrWhiteSpace(options.FromAddress))
        {
            return ValidateOptionsResult.Fail(
                "Modules:Notification:Ses:FromAddress must be set outside Development and Testing.");
        }

        return MailAddress.TryCreate(options.FromAddress, out var address)
               && string.Equals(address.Address, options.FromAddress, StringComparison.OrdinalIgnoreCase)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Modules:Notification:Ses:FromAddress must be a valid email address.");
    }
}
