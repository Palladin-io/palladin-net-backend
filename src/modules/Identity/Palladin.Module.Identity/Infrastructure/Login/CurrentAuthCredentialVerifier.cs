using Microsoft.AspNetCore.Http;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.PasswordAuth;

namespace Palladin.Module.Identity.Infrastructure.Login;

// Step-up re-verification shares the login lockout so a stolen access token is not an unthrottled password oracle.
internal sealed class CurrentAuthCredentialVerifier(
    IPasswordHasher passwordHasher,
    LoginThrottleService throttle,
    IClock clock)
{
    public async Task<CurrentAuthCredentialResult> VerifyAsync(
        User user,
        PasswordCredential passwordCredential,
        byte[] currentAuthCredential,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        var status = await throttle.GetStatusAsync(user.Email, now, ct);
        if (status.IsLocked)
        {
            return CurrentAuthCredentialResult.Locked(status.RetryAfterSeconds);
        }

        if (passwordHasher.Verify(currentAuthCredential, passwordCredential.AuthHash, passwordCredential.ServerHashSalt))
        {
            return CurrentAuthCredentialResult.Verified();
        }

        var failure = await throttle.RecordFailureAsync(
            user.Email,
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            new LoginFailureAttribution(
                httpContext.User.GetOrganizationId(),
                user.Id,
                LoginAttemptFactor.Password,
                user.PreferredLanguage.Code,
                user.EmailVerified),
            now,
            ct);

        return failure.IsLocked
            ? CurrentAuthCredentialResult.Locked(failure.RetryAfterSeconds)
            : CurrentAuthCredentialResult.Rejected();
    }
}

internal readonly record struct CurrentAuthCredentialResult(bool IsVerified, int? RetryAfterSeconds)
{
    internal static CurrentAuthCredentialResult Verified() => new(true, null);

    internal static CurrentAuthCredentialResult Rejected() => new(false, null);

    internal static CurrentAuthCredentialResult Locked(int retryAfterSeconds) => new(false, retryAfterSeconds);
}
