using System.Globalization;
using System.Text.Json.Serialization;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;
using Palladin.Core.Api;
using Palladin.Core.Guid;
using Palladin.Core.Json;
using Palladin.Core.Security;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Login;
using Palladin.Module.Identity.Infrastructure.PasswordAuth;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Infrastructure.SharedUnlock;

using Palladin.Module.Identity.Shared;

namespace Palladin.Module.Identity.Features;

internal sealed class AuthorizeSharedUnlockOperation(IdentityDomainWriteContext context,
    IPasswordHasher passwordHasher, LoginRateLimiter rateLimiter, LoginThrottleService throttle,
    IGuidProvider guidProvider, IClock clock)
{
    public async Task<IdentityOperationResult<AuthorizeSharedUnlockResponse>> ExecuteAsync(AuthorizeSharedUnlockRequest req, HttpContext httpContext, CancellationToken ct)
    {
        var userId = httpContext.User.GetUserId();
        var organizationId = httpContext.User.GetOrganizationId();
        var now = clock.GetCurrentInstant();
        var hash = TokenService.HashToken(req.RefreshToken);
        var session = await context.RefreshTokens.SingleOrDefaultAsync(token =>
            token.UserId == userId && token.OrganizationId == organizationId && token.TokenHash == hash, ct);
        if (session is null || !session.IsActive(now))
        {
            return IdentityOperationResult<AuthorizeSharedUnlockResponse>.Status(401);
        }

        var user = await context.Users.Include(user => user.PasswordCredential)
            .Include(user => user.TotpCredential).SingleAsync(user => user.Id == userId, ct);
        var membership = await context.OrganizationMembers.SingleOrDefaultAsync(member =>
            member.UserId == userId && member.OrganizationId == organizationId, ct);
        if (membership is null || membership.Status != OrganizationMemberStatus.Active
            || membership.AuthorizationVersion != session.AuthorizationVersion
            || membership.AuthorizationVersion != httpContext.User.GetAuthorizationVersion())
        {
            return IdentityOperationResult<AuthorizeSharedUnlockResponse>.Status(401);
        }

        var sessionRevocation = await SharedUnlockSessionRevocation.LoadAsync(context, session, ct);
        if (sessionRevocation.IsRevoked)
        {
            return IdentityOperationResult<AuthorizeSharedUnlockResponse>.Status(401);
        }
        sessionRevocation.Fence(context);

        if (user.SharedUnlockRevision != req.ExpectedPreferenceRevision
            || user.CredentialRevision != req.ExpectedCredentialRevision
            || user.PrivateKeyWrapRevision != req.ExpectedPrivateKeyWrapRevision)
        {
            return SendConflictAsync(ct);
        }

        if (!user.IsOnboarded || user.SecurityVersion != IdentityKdfProfiles.CurrentSecurityVersion
            || user.KdfProfileId != IdentityKdfProfiles.CurrentProfileId || user.PasswordCredential is null)
        {
            return IdentityOperationResult<AuthorizeSharedUnlockResponse>.Status(401);
        }

        if (!session.SatisfiesSecondFactor(user.TotpCredential, now))
        {
            return IdentityOperationResult<AuthorizeSharedUnlockResponse>.Failure(StatusCodes.Status403Forbidden, ErrorResponses.General("shared-unlock-step-up-required"));
        }

        var idle = Instant.FromUnixTimeMilliseconds(req.IdleDeadlineMs);
        var absolute = Instant.FromUnixTimeMilliseconds(req.AbsoluteDeadlineMs);
        var offline = Instant.FromUnixTimeMilliseconds(req.OfflineDeadlineMs);
        if (!SharedUnlockAuthorization.ValidDeadlines(idle, absolute, offline, Instant.MaxValue, now))
        {
            return SendConflictAsync(ct);
        }

        var sessionExpiry = Instant.FromUnixTimeMilliseconds(session.ExpiresAt.ToUnixTimeMilliseconds());
        absolute = new[] { absolute, sessionExpiry }.Min();
        idle = new[] { idle, absolute }.Min();
        offline = new[] { offline, sessionExpiry }.Min();
        if (!SharedUnlockAuthorization.ValidDeadlines(idle, absolute, offline, session.ExpiresAt, now))
        {
            return SendConflictAsync(ct);
        }

        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var permit = await rateLimiter.AcquireLoginAsync(user.Email, ip, now, ct);
        if (!permit.IsAcquired)
        {
            return SendRateLimitedAsync(permit.RetryAfterSeconds, ct);
        }

        var status = await throttle.GetStatusAsync(user.Email, now, ct);
        if (status.IsLocked)
        {
            return SendRateLimitedAsync(status.RetryAfterSeconds, ct);
        }

        bool verified;
        try
        {
            verified = passwordHasher.Verify(req.AuthCredential,
                user.PasswordCredential.AuthHash, user.PasswordCredential.ServerHashSalt);
        }
        finally
        {
            Array.Clear(req.AuthCredential);
        }

        if (!verified)
        {
            var failure = await throttle.RecordFailureAsync(user.Email, ip,
                LoginFailureAttribution.Known(membership.OrganizationId, user.Id, LoginAttemptFactor.Password,
                    user.PreferredLanguage.Code, user.EmailVerified), now, ct);
            if (failure.IsLocked)
            {
                return SendRateLimitedAsync(failure.RetryAfterSeconds, ct);
            }

            return IdentityOperationResult<AuthorizeSharedUnlockResponse>.Status(401);
        }

        var reset = await throttle.StageResetAsync(context, user.Email, now, ct);
        if (reset.IsLocked)
        {
            return SendRateLimitedAsync(reset.RetryAfterSeconds, ct);
        }

        if (!user.TryAdvanceSharedUnlockSequence())
        {
            return SendConflictAsync(ct);
        }

        var sessionId = session.SessionId ?? session.Id;
        var authorization = sessionRevocation.Authorization;
        if (authorization is null)
        {
            authorization = SharedUnlockAuthorization.Create(user.Id, sessionId);
            context.Add(authorization);
        }

        authorization.AuthorizeManualUnlock(guidProvider.Generate(), user, session, req.SourceGeneration,
            idle, absolute, offline, now);
        context.MarkPropertyAsUpdated(session, session => session.RevokedAt);
        context.MarkPropertyAsUpdated(membership, member => member.Status);
        if (user.TotpCredential is { } factor)
        {
            context.MarkPropertyAsUpdated(factor, factor => factor.ConfigurationRevision);
        }

        if (!authorization.IsSessionCurrent(user, session, membership, user.TotpCredential, clock.GetCurrentInstant()))
        {
            return SendConflictAsync(ct);
        }

        try
        {
            await context.CommitAsync(ct);
        }
        catch (Exception exception) when (LoginProtectionConcurrency.IsAuthenticationFenceConflict(exception)
            || exception is DbUpdateException { InnerException: PostgresException
                { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "PK_SharedUnlockAuthorizations" } })
        {
            return SendConflictAsync(ct);
        }

        return IdentityOperationResult<AuthorizeSharedUnlockResponse>.Ok(new AuthorizeSharedUnlockResponse(authorization.Id, authorization.Sequence,
            user.Id, authorization.OrganizationId, authorization.CredentialRevision,
            authorization.PrivateKeyWrapRevision, authorization.AuthorizationVersion,
            authorization.UnlockedAt.ToUnixTimeMilliseconds(), authorization.IdleDeadline.ToUnixTimeMilliseconds(),
            authorization.AbsoluteDeadline.ToUnixTimeMilliseconds(), authorization.OfflineDeadline.ToUnixTimeMilliseconds()));
    }

    private static IdentityOperationResult<AuthorizeSharedUnlockResponse> SendConflictAsync(CancellationToken ct)
    {
        return IdentityOperationResult<AuthorizeSharedUnlockResponse>.Failure(StatusCodes.Status409Conflict, ErrorResponses.General("shared-unlock-authorization-conflict"));
    }

    private static IdentityOperationResult<AuthorizeSharedUnlockResponse> SendRateLimitedAsync(int seconds, CancellationToken ct)
    {
        return new IdentityOperationResult<AuthorizeSharedUnlockResponse>(429, RetryAfterSeconds: seconds);
    }
}
