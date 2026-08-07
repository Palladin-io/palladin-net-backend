using System.Security.Cryptography;
using System.Text;
using JetBrains.Annotations;
using Microsoft.Extensions.Options;
using OtpNet;

namespace Palladin.Module.Identity.Infrastructure.Totp;

[UsedImplicitly]
internal sealed class TotpService(IOptions<TotpOptions> options) : ITotpService
{
    public string GenerateSecret() =>
        Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(options.Value.SecretSizeBytes));

    public string BuildOtpAuthUri(string secretBase32, string accountEmail)
    {
        var issuer = Uri.EscapeDataString(options.Value.Issuer);
        var label = Uri.EscapeDataString($"{options.Value.Issuer}:{accountEmail}");
        return $"otpauth://totp/{label}?secret={secretBase32}&issuer={issuer}&algorithm=SHA1&digits=6&period=30";
    }

    public bool VerifyCode(string secretBase32, string code, long minTimeStep, out long matchedTimeStep)
    {
        matchedTimeStep = 0;
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var totp = new OtpNet.Totp(Base32Encoding.ToBytes(secretBase32));
        var window = new VerificationWindow(
            previous: options.Value.VerificationWindowSteps,
            future: options.Value.VerificationWindowSteps);

        if (!totp.VerifyTotp(code.Trim(), out matchedTimeStep, window))
        {
            return false;
        }

        // Reject a code from a step already consumed (replay within the window).
        return matchedTimeStep > minTimeStep;
    }

    public IReadOnlyList<string> GenerateRecoveryCodes() =>
        Enumerable.Range(0, options.Value.RecoveryCodeCount)
            .Select(_ => Base32Encoding.ToString(RandomNumberGenerator.GetBytes(10)).TrimEnd('='))
            .ToArray();

    public string HashRecoveryCode(string code)
    {
        var normalized = new string(code.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }
}
