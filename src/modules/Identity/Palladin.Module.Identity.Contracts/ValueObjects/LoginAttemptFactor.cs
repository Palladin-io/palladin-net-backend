using JetBrains.Annotations;

namespace Palladin.Module.Identity.Contracts.ValueObjects;

[PublicAPI]
public static class LoginAttemptFactor
{
    public const string Password = "password";
    public const string Totp = "totp";
}
