namespace Palladin.Module.Identity.Infrastructure.Totp;

internal interface ITotpService
{
    string GenerateSecret();

    string BuildOtpAuthUri(string secretBase32, string accountEmail);

    // Verifies a 6-digit code within the configured window. matchedTimeStep is the accepted step,
    // used to block replay of an already-consumed code.
    bool VerifyCode(string secretBase32, string code, long minTimeStep, out long matchedTimeStep);

    IReadOnlyList<string> GenerateRecoveryCodes();

    string HashRecoveryCode(string code);
}
