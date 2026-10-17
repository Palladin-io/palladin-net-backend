using Palladin.Core.Guid;
using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Login;
using Palladin.Module.Identity.Infrastructure.PasswordAuth;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Infrastructure.Waitlist;
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

using Palladin.Module.Identity.Shared;

namespace Palladin.Module.Identity.Features;

internal sealed class LoginOperation(
    IdentityDomainWriteContext domainWriteContext,
    IPasswordHasher passwordHasher,
    IAuthSessionIssuer sessionIssuer,
    LoginRateLimiter loginRateLimiter,
    LoginThrottleService loginThrottle,
    IGuidProvider guidProvider,
    IOptions<TotpOptions> totpOptions,
    WaitlistDeveloperBenefitActivator waitlistDeveloperBenefitActivator,
    IClock clock)
{
    private const int ConcurrentAuthRetryAfterSeconds = 1;

    // Fixed material to equalise verification time when no password credential exists, so a missing
    // account is timing-indistinguishable from a wrong authHash.
    private static readonly byte[] DummyHash = new byte[32];
    private static readonly byte[] DummySalt = new byte[16];

    public async Task<IdentityOperationResult<LoginResponse>> ExecuteAsync(LoginRequest req, HttpContext httpContext, CancellationToken ct)
    {
        var email = req.Email.Trim().ToLowerInvariant();
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var now = clock.GetCurrentInstant();

        var rateLimitLease = await loginRateLimiter.AcquireLoginAsync(email, ip, now, ct);
        if (!rateLimitLease.IsAcquired)
        {
            return SendRateLimitedAsync(rateLimitLease.RetryAfterSeconds, ct);
        }

        var throttleStatus = await loginThrottle.GetStatusAsync(email, now, ct);
        if (throttleStatus.IsLocked)
        {
            return SendRateLimitedAsync(throttleStatus.RetryAfterSeconds, ct);
        }

        // Load ONLY the password credential before verifying — pulling the full aggregate (roles, org,
        // totp) only for known emails would leak account existence through response time that the
        // dummy-hash compensation below cannot mask.
        var credentialState = await domainWriteContext.PasswordCredentials
            .Where(credential => credential.User.Email == email)
            .Select(credential => new
            {
                Credential = credential,
                credential.UserId,
                credential.User.OrganizationId,
                credential.User.PreferredLanguage,
                credential.User.EmailVerified,
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
            var attribution = credentialState is null
                ? LoginFailureAttribution.Unknown(LoginAttemptFactor.Password)
                : LoginFailureAttribution.Known(
                    credentialState.OrganizationId,
                    credentialState.UserId,
                    LoginAttemptFactor.Password,
                    credentialState.PreferredLanguage.Code,
                    credentialState.EmailVerified);
            var failure = await loginThrottle.RecordFailureAsync(email, ip, attribution, now, ct);
            if (failure.IsLocked)
            {
                return SendRateLimitedAsync(failure.RetryAfterSeconds, ct);
            }

            return IdentityOperationResult<LoginResponse>.Status(401);
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
            return IdentityOperationResult<LoginResponse>.Status(401);
        }

        if (user.TotpCredential is { IsEnabled: true })
        {
            var (challengeToken, challengeHash) = SecureToken.Generate();
            domainWriteContext.Add(VerificationToken.CreateLoginChallenge(
                guidProvider.Generate(), user.Id, challengeHash,
                Duration.FromMinutes(totpOptions.Value.ChallengeTtlMinutes), now));
            await domainWriteContext.CommitAsync(ct);

            return IdentityOperationResult<LoginResponse>.Ok(new LoginResponse(true, challengeToken, null, null, null, null, null, null, null));
        }

        var reset = await loginThrottle.StageResetAsync(domainWriteContext, email, now, ct);
        if (reset.IsLocked)
        {
            return SendRateLimitedAsync(reset.RetryAfterSeconds, ct);
        }

        await using var transaction = await domainWriteContext.BeginTransactionAsync(ct);
        await waitlistDeveloperBenefitActivator.TryActivateAsync(user, now, ct);

        var (accessToken, refreshToken) = sessionIssuer.Issue(
            user,
            user.Organization,
            membership.EffectivePermissions(),
            membership.AuthorizationVersion,
            now);
        try
        {
            await domainWriteContext.CommitAsync(transaction, ct);
        }
        catch (Exception exception) when (LoginProtectionConcurrency.IsAuthenticationFenceConflict(exception))
        {
            return SendRateLimitedAsync(ConcurrentAuthRetryAfterSeconds, ct);
        }

        return IdentityOperationResult<LoginResponse>.Ok(
            new LoginResponse(
                false,
                null,
                accessToken,
                refreshToken,
                user.Id,
                user.IsOnboarded,
                user.EmailVerified,
                user.ActiveWaitlistDeveloperBenefitStartedAt(now),
                user.ActiveWaitlistDeveloperBenefitEndsAt(now)));
    }

    private static IdentityOperationResult<LoginResponse> SendRateLimitedAsync(int retryAfterSeconds, CancellationToken ct)
    {
        return new IdentityOperationResult<LoginResponse>(429, RetryAfterSeconds: retryAfterSeconds);
    }
}
