using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Module.Identity.Domain.Enums;
using Palladin.Module.Identity.Infrastructure;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Login;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Infrastructure.Totp;
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

[PublicAPI]
public sealed record LoginTotpRequest
{
    public string ChallengeToken { get; init; } = string.Empty;
    public string Code { get; init; } = string.Empty;
}

[UsedImplicitly]
internal sealed class LoginTotpValidator : Validator<LoginTotpRequest>
{
    public LoginTotpValidator()
    {
        RuleFor(x => x.ChallengeToken).NotEmpty();
        RuleFor(x => x.Code).NotEmpty();
    }
}

[PublicAPI]
internal sealed class LoginTotpEndpoint(
    IdentityDomainReadContext domainReadContext,
    IdentityDomainWriteContext domainWriteContext,
    ITotpService totpService,
    IAuthSessionIssuer sessionIssuer,
    ILoginRateLimiter loginRateLimiter,
    ILoginThrottleService loginThrottle,
    IClock clock) : Endpoint<LoginTotpRequest, AuthSessionResponse>
{
    public override void Configure()
    {
        Post("api/auth/login/totp");
        // Anonymous by design: completes a login that already passed the password step, keyed by the
        // single-use challenge token. Accepts a TOTP code or a recovery code; attempts are rate-limited.
        AllowAnonymous();
        Summary(summary =>
        {
            summary.Summary = "Complete login with a TOTP or recovery code";
            summary.Description = "Redeems the short-lived login challenge with a 6-digit TOTP code (±1 window, "
                + "replay-protected) or a single-use recovery code, then issues the session.";
        });
        Tags("Identity/Auth");
    }

    public override async Task HandleAsync(LoginTotpRequest req, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        var challengeHash = SecureToken.Hash(req.ChallengeToken);
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        var ipLease = await loginRateLimiter.AcquireTotpIpAsync(ip, now, ct);
        if (!ipLease.IsAcquired)
        {
            await SendRateLimitedAsync(ipLease.RetryAfterSeconds, ct);
            return;
        }

        var challengeOwner = await domainReadContext.VerificationTokens
            .Where(t => t.TokenHash == challengeHash
                        && t.Purpose == VerificationTokenPurpose.LoginTotpChallenge
                        && t.ConsumedAt == null
                        && t.ExpiresAt >= now)
            .Select(t => new { t.Id, t.UserId, t.User.Email })
            .FirstOrDefaultAsync(ct);

        if (challengeOwner is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var accountLease = await loginRateLimiter.AcquireTotpAccountAsync(challengeOwner.Email, now, ct);
        if (!accountLease.IsAcquired)
        {
            await SendRateLimitedAsync(accountLease.RetryAfterSeconds, ct);
            return;
        }

        var throttleStatus = await loginThrottle.GetStatusAsync(challengeOwner.Email, ip, now, ct);
        if (throttleStatus.IsLocked)
        {
            await SendRateLimitedAsync(throttleStatus.RetryAfterSeconds, ct);
            return;
        }

        var challenge = await domainWriteContext.VerificationTokens
            .FirstOrDefaultAsync(
                t => t.Id == challengeOwner.Id && t.Purpose == VerificationTokenPurpose.LoginTotpChallenge, ct);

        if (challenge is null || !challenge.CanConsume(now))
        {
            await Send.UnauthorizedAsync(ct);
            return;
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
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var membership = user.OrganizationMemberships.SingleOrDefault(
            x => x.OrganizationId == user.OrganizationId);
        if (membership is null || membership.Status != OrganizationMemberStatus.Active)
        {
            await Send.UnauthorizedAsync(ct);
            return;
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
            var failure = await loginThrottle.RecordFailureAsync(user.Email, ip, now, ct);
            if (failure.IsLocked)
            {
                await SendRateLimitedAsync(failure.RetryAfterSeconds, ct);
                return;
            }

            await Send.UnauthorizedAsync(ct);
            return;
        }

        var reset = await loginThrottle.ResetAsync(user.Email, ip, now, ct);
        if (reset.IsLocked)
        {
            await SendRateLimitedAsync(reset.RetryAfterSeconds, ct);
            return;
        }

        challenge.Consume(now);

        var (accessToken, refreshToken) = sessionIssuer.Issue(
            user,
            user.OrganizationId,
            membership.EffectivePermissions(),
            user.Organization.PlanType,
            membership.AuthorizationVersion,
            now);
        await domainWriteContext.CommitAsync(ct);

        await Send.OkAsync(
            new AuthSessionResponse(accessToken, refreshToken, user.Id, user.IsOnboarded, user.EmailVerified), ct);
    }

    private async Task SendRateLimitedAsync(int retryAfterSeconds, CancellationToken ct)
    {
        HttpContext.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        await Send.StatusCodeAsync(StatusCodes.Status429TooManyRequests, ct);
    }
}
