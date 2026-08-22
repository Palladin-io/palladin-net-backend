using Palladin.Core.Guid;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Login;
using Palladin.Module.Identity.Infrastructure.PasswordAuth;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Infrastructure.Totp;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using System.Text.Json.Serialization;
using Palladin.Core.Json;
using System.Globalization;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record LoginRequest
{
    public string Email { get; init; } = string.Empty;
    public ushort SecurityVersion { get; init; }
    public string KdfProfileId { get; init; } = string.Empty;
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] AuthCredential { get; init; } = [];
}

// When TotpRequired is true only ChallengeToken is set; otherwise the session fields are populated.
[PublicAPI]
public sealed record LoginResponse(
    bool TotpRequired,
    string? ChallengeToken,
    string? AccessToken,
    string? RefreshToken,
    Guid? UserId,
    bool? IsOnboarded,
    bool? EmailVerified);

[UsedImplicitly]
internal sealed class LoginValidator : Validator<LoginRequest>
{
    public LoginValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.SecurityVersion).Equal(IdentityKdfProfiles.CurrentSecurityVersion);
        RuleFor(x => x.KdfProfileId).Equal(IdentityKdfProfiles.CurrentProfileId);
        RuleFor(x => x.AuthCredential).Must(value => value is
            { Length: IdentityKdfProfiles.AuthCredentialBytes });
    }
}

[PublicAPI]
internal sealed class LoginEndpoint(
    IdentityDomainWriteContext domainWriteContext,
    IPasswordHasher passwordHasher,
    IAuthSessionIssuer sessionIssuer,
    ILoginRateLimiter loginRateLimiter,
    ILoginThrottleService loginThrottle,
    IGuidProvider guidProvider,
    IOptions<TotpOptions> totpOptions,
    IClock clock) : Endpoint<LoginRequest, LoginResponse>
{
    private const int ConcurrentAuthRetryAfterSeconds = 1;

    // Fixed material to equalise verification time when no password credential exists, so a missing
    // account is timing-indistinguishable from a wrong authHash.
    private static readonly byte[] DummyHash = new byte[32];
    private static readonly byte[] DummySalt = new byte[16];

    public override void Configure()
    {
        Post("api/auth/login");
        // Anonymous by design: this IS the authentication. Responses are generic to avoid account
        // enumeration; failures are rate-limited and locked out per (email, ip).
        AllowAnonymous();
        Summary(summary =>
        {
            summary.Summary = "Log in with email + password";
            summary.Description = "Verifies the client authHash (constant-time). Returns a session, or a "
                + "short-lived TOTP challenge when the second factor is enabled. Bad credentials return a "
                + "generic 401; repeated failures per (email, ip) are locked out with 429.";
        });
        Tags("Identity/Auth");
    }

    public override async Task HandleAsync(LoginRequest req, CancellationToken ct)
    {
        var email = req.Email.Trim().ToLowerInvariant();
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var now = clock.GetCurrentInstant();

        var rateLimitLease = await loginRateLimiter.AcquireLoginAsync(email, ip, now, ct);
        if (!rateLimitLease.IsAcquired)
        {
            await SendRateLimitedAsync(rateLimitLease.RetryAfterSeconds, ct);
            return;
        }

        var throttleStatus = await loginThrottle.GetStatusAsync(email, ip, now, ct);
        if (throttleStatus.IsLocked)
        {
            await SendRateLimitedAsync(throttleStatus.RetryAfterSeconds, ct);
            return;
        }

        // Load ONLY the password credential before verifying — pulling the full aggregate (roles, org,
        // totp) only for known emails would leak account existence through response time that the
        // dummy-hash compensation below cannot mask.
        var credentialState = await domainWriteContext.PasswordCredentials
            .Where(credential => credential.User.Email == email)
            .Select(credential => new
            {
                Credential = credential,
                credential.User.SecurityVersion,
                credential.User.KdfProfileId,
            })
            .FirstOrDefaultAsync(ct);
        var credential = credentialState?.Credential;

        var credentialVerified = passwordHasher.Verify(
            req.AuthCredential,
            credential?.AuthHash ?? DummyHash,
            credential?.ServerHashSalt ?? DummySalt);
        if (credential is null
            || credentialState!.SecurityVersion != req.SecurityVersion
            || credentialState.KdfProfileId != req.KdfProfileId
            || !credentialVerified)
        {
            var failure = await loginThrottle.RecordFailureAsync(email, ip, now, ct);
            if (failure.IsLocked)
            {
                await SendRateLimitedAsync(failure.RetryAfterSeconds, ct);
                return;
            }

            await Send.UnauthorizedAsync(ct);
            return;
        }

        // Authenticated — now it is safe to load the rest of the aggregate.
        var user = await domainWriteContext.Users
            .Include(u => u.OrganizationMemberships)
                .ThenInclude(m => m.RoleAssignments)
                    .ThenInclude(assignment => assignment.Role)
            .Include(u => u.Organization)
            .Include(u => u.TotpCredential)
            .FirstAsync(u => u.Id == credential.UserId, ct);

        var membership = user.OrganizationMemberships.SingleOrDefault(
            x => x.OrganizationId == user.OrganizationId);
        if (membership is null || membership.Status != OrganizationMemberStatus.Active)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        if (user.TotpCredential is { IsEnabled: true })
        {
            var (challengeToken, challengeHash) = SecureToken.Generate();
            domainWriteContext.Add(VerificationToken.CreateLoginChallenge(
                guidProvider.Generate(), user.Id, challengeHash,
                Duration.FromMinutes(totpOptions.Value.ChallengeTtlMinutes), now));
            await domainWriteContext.CommitAsync(ct);

            await Send.OkAsync(new LoginResponse(true, challengeToken, null, null, null, null, null), ct);
            return;
        }

        var reset = await loginThrottle.StageResetAsync(domainWriteContext, email, ip, now, ct);
        if (reset.IsLocked)
        {
            await SendRateLimitedAsync(reset.RetryAfterSeconds, ct);
            return;
        }

        var (accessToken, refreshToken) = sessionIssuer.Issue(
            user,
            user.OrganizationId,
            membership.EffectivePermissions(),
            user.Organization.PlanType,
            membership.AuthorizationVersion,
            now);
        try
        {
            await domainWriteContext.CommitAsync(ct);
        }
        catch (Exception exception) when (LoginProtectionConcurrency.IsAuthenticationFenceConflict(exception))
        {
            await SendRateLimitedAsync(ConcurrentAuthRetryAfterSeconds, ct);
            return;
        }

        await Send.OkAsync(
            new LoginResponse(false, null, accessToken, refreshToken, user.Id, user.IsOnboarded, user.EmailVerified),
            ct);
    }

    private async Task SendRateLimitedAsync(int retryAfterSeconds, CancellationToken ct)
    {
        HttpContext.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        await Send.StatusCodeAsync(StatusCodes.Status429TooManyRequests, ct);
    }
}
