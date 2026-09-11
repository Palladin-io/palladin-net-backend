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

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record AuthorizeSharedUnlockRequest
{
    public string RefreshToken { get; init; } = string.Empty;
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))]
    public byte[] AuthCredential { get; init; } = [];
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))]
    public byte[] SourceGeneration { get; init; } = [];
    public uint ExpectedPreferenceRevision { get; init; }
    public uint ExpectedCredentialRevision { get; init; }
    public uint ExpectedPrivateKeyWrapRevision { get; init; }
    public long IdleDeadlineMs { get; init; }
    public long AbsoluteDeadlineMs { get; init; }
    public long OfflineDeadlineMs { get; init; }
}

[PublicAPI]
public sealed record AuthorizeSharedUnlockResponse(Guid AuthorizationId, uint Sequence,
    Guid AccountId, Guid OrganizationId, uint CredentialRevision, uint PrivateKeyWrapRevision,
    uint AuthorizationVersion, long UnlockedAtMs, long IdleDeadlineMs,
    long AbsoluteDeadlineMs, long OfflineDeadlineMs);

[UsedImplicitly]
internal sealed class AuthorizeSharedUnlockValidator : Validator<AuthorizeSharedUnlockRequest>
{
    public AuthorizeSharedUnlockValidator()
    {
        RuleFor(request => request.RefreshToken).NotEmpty().MaximumLength(1024);
        RuleFor(request => request.AuthCredential).Must(value => value is { Length: IdentityKdfProfiles.AuthCredentialBytes });
        RuleFor(request => request.SourceGeneration).Must(value => value is { Length: 32 });
        RuleFor(request => request.ExpectedPreferenceRevision).GreaterThan(0u);
        RuleFor(request => request.ExpectedCredentialRevision).GreaterThan(0u);
        RuleFor(request => request.ExpectedPrivateKeyWrapRevision).GreaterThan(0u);
        RuleFor(request => request.IdleDeadlineMs).InclusiveBetween(1, Instant.MaxValue.ToUnixTimeMilliseconds());
        RuleFor(request => request.AbsoluteDeadlineMs).InclusiveBetween(1, Instant.MaxValue.ToUnixTimeMilliseconds());
        RuleFor(request => request.OfflineDeadlineMs).InclusiveBetween(1, Instant.MaxValue.ToUnixTimeMilliseconds());
    }
}

[PublicAPI]
internal sealed class AuthorizeSharedUnlockEndpoint(IdentityDomainWriteContext context,
    IPasswordHasher passwordHasher, LoginRateLimiter rateLimiter, LoginThrottleService throttle,
    IGuidProvider guidProvider, IClock clock)
    : Endpoint<AuthorizeSharedUnlockRequest, AuthorizeSharedUnlockResponse>
{
    public override void Configure()
    {
        Post("api/account/shared-unlock/authorizations");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Tags("Identity/Account");
        Summary(summary => summary.Summary = "Authorize a fresh manual unlock using the source's own session and password proof");
    }

    public override async Task HandleAsync(AuthorizeSharedUnlockRequest req, CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var userId = User.GetUserId();
        var organizationId = User.GetOrganizationId();
        var now = clock.GetCurrentInstant();
        var hash = TokenService.HashToken(req.RefreshToken);
        var session = await context.RefreshTokens.SingleOrDefaultAsync(token =>
            token.UserId == userId && token.OrganizationId == organizationId && token.TokenHash == hash, ct);
        if (session is null || !session.IsActive(now))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var user = await context.Users.Include(user => user.PasswordCredential)
            .Include(user => user.TotpCredential).SingleAsync(user => user.Id == userId, ct);
        var membership = await context.OrganizationMembers.SingleOrDefaultAsync(member =>
            member.UserId == userId && member.OrganizationId == organizationId, ct);
        if (membership is null || membership.Status != OrganizationMemberStatus.Active
            || membership.AuthorizationVersion != session.AuthorizationVersion
            || membership.AuthorizationVersion != User.GetAuthorizationVersion())
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var sessionRevocation = await SharedUnlockSessionRevocation.LoadAsync(context, session, ct);
        if (sessionRevocation.IsRevoked)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }
        sessionRevocation.Fence(context);

        if (user.SharedUnlockRevision != req.ExpectedPreferenceRevision
            || user.CredentialRevision != req.ExpectedCredentialRevision
            || user.PrivateKeyWrapRevision != req.ExpectedPrivateKeyWrapRevision)
        {
            await SendConflictAsync(ct);
            return;
        }

        if (!user.IsOnboarded || user.SecurityVersion != IdentityKdfProfiles.CurrentSecurityVersion
            || user.KdfProfileId != IdentityKdfProfiles.CurrentProfileId || user.PasswordCredential is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        if (!session.SatisfiesSecondFactor(user.TotpCredential, now))
        {
            AddError(ErrorResponses.General("shared-unlock-step-up-required"));
            await Send.ErrorsAsync(StatusCodes.Status403Forbidden, ct);
            return;
        }

        var idle = Instant.FromUnixTimeMilliseconds(req.IdleDeadlineMs);
        var absolute = Instant.FromUnixTimeMilliseconds(req.AbsoluteDeadlineMs);
        var offline = Instant.FromUnixTimeMilliseconds(req.OfflineDeadlineMs);
        if (!SharedUnlockAuthorization.ValidDeadlines(idle, absolute, offline, Instant.MaxValue, now))
        {
            await SendConflictAsync(ct);
            return;
        }

        var sessionExpiry = Instant.FromUnixTimeMilliseconds(session.ExpiresAt.ToUnixTimeMilliseconds());
        absolute = new[] { absolute, sessionExpiry }.Min();
        idle = new[] { idle, absolute }.Min();
        offline = new[] { offline, sessionExpiry }.Min();
        if (!SharedUnlockAuthorization.ValidDeadlines(idle, absolute, offline, session.ExpiresAt, now))
        {
            await SendConflictAsync(ct);
            return;
        }

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var permit = await rateLimiter.AcquireLoginAsync(user.Email, ip, now, ct);
        if (!permit.IsAcquired)
        {
            await SendRateLimitedAsync(permit.RetryAfterSeconds, ct);
            return;
        }

        var status = await throttle.GetStatusAsync(user.Email, now, ct);
        if (status.IsLocked)
        {
            await SendRateLimitedAsync(status.RetryAfterSeconds, ct);
            return;
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
                await SendRateLimitedAsync(failure.RetryAfterSeconds, ct);
                return;
            }

            await Send.UnauthorizedAsync(ct);
            return;
        }

        var reset = await throttle.StageResetAsync(context, user.Email, now, ct);
        if (reset.IsLocked)
        {
            await SendRateLimitedAsync(reset.RetryAfterSeconds, ct);
            return;
        }

        if (!user.TryAdvanceSharedUnlockSequence())
        {
            await SendConflictAsync(ct);
            return;
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
            await SendConflictAsync(ct);
            return;
        }

        try
        {
            await context.CommitAsync(ct);
        }
        catch (Exception exception) when (LoginProtectionConcurrency.IsAuthenticationFenceConflict(exception)
            || exception is DbUpdateException { InnerException: PostgresException
                { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "PK_SharedUnlockAuthorizations" } })
        {
            await SendConflictAsync(ct);
            return;
        }

        await Send.OkAsync(new AuthorizeSharedUnlockResponse(authorization.Id, authorization.Sequence,
            user.Id, authorization.OrganizationId, authorization.CredentialRevision,
            authorization.PrivateKeyWrapRevision, authorization.AuthorizationVersion,
            authorization.UnlockedAt.ToUnixTimeMilliseconds(), authorization.IdleDeadline.ToUnixTimeMilliseconds(),
            authorization.AbsoluteDeadline.ToUnixTimeMilliseconds(), authorization.OfflineDeadline.ToUnixTimeMilliseconds()), ct);
    }

    private async Task SendConflictAsync(CancellationToken ct)
    {
        AddError(ErrorResponses.General("shared-unlock-authorization-conflict"));
        await Send.ErrorsAsync(StatusCodes.Status409Conflict, ct);
    }

    private async Task SendRateLimitedAsync(int seconds, CancellationToken ct)
    {
        HttpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        await Send.StatusCodeAsync(StatusCodes.Status429TooManyRequests, ct);
    }
}
