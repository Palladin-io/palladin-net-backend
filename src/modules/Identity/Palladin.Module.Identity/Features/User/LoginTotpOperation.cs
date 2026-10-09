using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Module.Identity.Domain.Enums;
using Palladin.Module.Identity.Infrastructure;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Login;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Infrastructure.Totp;
using Palladin.Module.Identity.Infrastructure.Waitlist;
using Palladin.Module.Identity.Shared;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Module.Identity.Domain;
using System.Globalization;


namespace Palladin.Module.Identity.Features;

internal sealed class LoginTotpOperation(
    IdentityDomainWriteContext domainWriteContext,
    ITotpService totpService,
    IAuthSessionIssuer sessionIssuer,
    LoginRateLimiter loginRateLimiter,
    LoginThrottleService loginThrottle,
    WaitlistDeveloperBenefitActivator waitlistDeveloperBenefitActivator,
    IClock clock)
{
    private const int ConcurrentAuthRetryAfterSeconds = 1;

    public async Task<IdentityOperationResult<AuthSessionResponse>> ExecuteAsync(LoginTotpRequest req, HttpContext httpContext, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        var challengeHash = SecureToken.Hash(req.ChallengeToken);
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        var ipLease = await loginRateLimiter.AcquireTotpIpAsync(ip, now, ct);
        if (!ipLease.IsAcquired)
        {
            return SendRateLimitedAsync(ipLease.RetryAfterSeconds, ct);
        }

        var challengeOwner = await domainWriteContext.VerificationTokens
            .Where(t => t.TokenHash == challengeHash
                        && t.Purpose == VerificationTokenPurpose.LoginTotpChallenge
                        && t.ConsumedAt == null
                        && t.ExpiresAt >= now)
            .Select(t => new { t.Id, t.UserId, t.User.Email })
            .FirstOrDefaultAsync(ct);

        if (challengeOwner is null)
        {
            return IdentityOperationResult<AuthSessionResponse>.Status(401);
        }

        var accountLease = await loginRateLimiter.AcquireTotpAccountAsync(challengeOwner.Email, now, ct);
        if (!accountLease.IsAcquired)
        {
            return SendRateLimitedAsync(accountLease.RetryAfterSeconds, ct);
        }

        var throttleStatus = await loginThrottle.GetStatusAsync(challengeOwner.Email, now, ct);
        if (throttleStatus.IsLocked)
        {
            return SendRateLimitedAsync(throttleStatus.RetryAfterSeconds, ct);
        }

        var challenge = await domainWriteContext.VerificationTokens
            .FirstOrDefaultAsync(
                t => t.Id == challengeOwner.Id && t.Purpose == VerificationTokenPurpose.LoginTotpChallenge, ct);

        if (challenge is null || !challenge.CanConsume(now))
        {
            return IdentityOperationResult<AuthSessionResponse>.Status(401);
        }

        var user = await domainWriteContext.Users
            .Include(u => u.OrganizationMemberships)
                .ThenInclude(m => m.RoleAssignments)
                    .ThenInclude(assignment => assignment.Role)
            .Include(u => u.Organization)
            .Include(u => u.TotpCredential).ThenInclude(t => t!.RecoveryCodes)
            .FirstOrDefaultAsync(u => u.Id == challengeOwner.UserId, ct);

        if (user?.TotpCredential is not { IsEnabled: true, Secret: { } secret } totp)
        {
            return IdentityOperationResult<AuthSessionResponse>.Status(401);
        }

        var membership = user.OrganizationMemberships.SingleOrDefault(
            x => x.OrganizationId == user.OrganizationId);
        if (membership is null || membership.Status != OrganizationMemberStatus.Active)
        {
            return IdentityOperationResult<AuthSessionResponse>.Status(401);
        }

        if (totpService.VerifyCode(secret, req.Code, totp.LastUsedTimeStep, out var matchedStep))
        {
            totp.RecordUsedTimeStep(matchedStep, now);
        }
        else if (totp.FindAvailableRecoveryCode(totpService.HashRecoveryCode(req.Code)) is { } recoveryCode)
        {
            recoveryCode.MarkUsed(now);
        }
        else
        {
            var failure = await loginThrottle.RecordFailureAsync(
                user.Email,
                ip,
                LoginFailureAttribution.Known(
                    user.OrganizationId,
                    user.Id,
                    LoginAttemptFactor.Totp,
                    user.PreferredLanguage.Code,
                    user.EmailVerified),
                now,
                ct);
            if (failure.IsLocked)
            {
                return SendRateLimitedAsync(failure.RetryAfterSeconds, ct);
            }

            return IdentityOperationResult<AuthSessionResponse>.Status(401);
        }

        var reset = await loginThrottle.StageResetAsync(domainWriteContext, user.Email, now, ct);
        if (reset.IsLocked)
        {
            return SendRateLimitedAsync(reset.RetryAfterSeconds, ct);
        }

        challenge.Consume(now);
        domainWriteContext.MarkPropertyAsUpdated(totp, factor => factor.ConfigurationRevision);
        await using var transaction = await domainWriteContext.BeginTransactionAsync(ct);
        await waitlistDeveloperBenefitActivator.TryActivateAsync(user, now, ct);

        var (accessToken, refreshToken) = sessionIssuer.Issue(
            user,
            user.Organization,
            membership.EffectivePermissions(),
            membership.AuthorizationVersion,
            now,
            totp.ConfigurationRevision,
            now);
        try
        {
            await domainWriteContext.CommitAsync(transaction, ct);
        }
        catch (Exception exception) when (LoginProtectionConcurrency.IsAuthenticationFenceConflict(exception))
        {
            return SendRateLimitedAsync(ConcurrentAuthRetryAfterSeconds, ct);
        }

        return IdentityOperationResult<AuthSessionResponse>.Ok(
            new AuthSessionResponse(
                accessToken,
                refreshToken,
                user.Id,
                user.IsOnboarded,
                user.EmailVerified,
                user.ActiveWaitlistDeveloperBenefitStartedAt(now),
                user.ActiveWaitlistDeveloperBenefitEndsAt(now)));
    }

    private static IdentityOperationResult<AuthSessionResponse> SendRateLimitedAsync(int retryAfterSeconds, CancellationToken ct)
    {
        return new IdentityOperationResult<AuthSessionResponse>(429, RetryAfterSeconds: retryAfterSeconds);
    }
}
