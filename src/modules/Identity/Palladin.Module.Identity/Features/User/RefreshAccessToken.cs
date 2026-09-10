using Palladin.Core.Guid;
using Palladin.Core.Api;
using Microsoft.AspNetCore.Http;
using Palladin.Module.Identity.Infrastructure.SharedUnlock;
using Palladin.Module.Identity.Domain;
using Palladin.Core.Security;
using Palladin.Module.Identity.Infrastructure.Options;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Waitlist;
using Palladin.Module.Identity.Contracts.ValueObjects;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record RefreshAccessTokenRequest
{
    public string RefreshToken { get; init; } = string.Empty;
}

[PublicAPI]
public sealed record RefreshAccessTokenResponse(
    string AccessToken,
    string RefreshToken,
    Guid UserId,
    bool IsOnboarded,
    bool EmailVerified,
    Instant? WaitlistDeveloperBenefitStartedAt,
    Instant? WaitlistDeveloperBenefitEndsAt);

[UsedImplicitly]
internal sealed class RefreshAccessTokenValidator : Validator<RefreshAccessTokenRequest>
{
    public RefreshAccessTokenValidator()
    {
        RuleFor(x => x.RefreshToken).NotEmpty();
    }
}

[PublicAPI]
internal sealed class RefreshAccessTokenEndpoint(
    IdentityDomainWriteContext domainWriteContext,
    ITokenService tokenService,
    RefreshTokenLineageRevoker lineageRevoker,
    IGuidProvider guidProvider,
    IClock clock,
    WaitlistDeveloperBenefitActivator waitlistDeveloperBenefitActivator,
    IOptions<JwtOptions> jwtOptions) : Endpoint<RefreshAccessTokenRequest, RefreshAccessTokenResponse>
{
    public override void Configure()
    {
        Post("api/auth/refresh");
        AllowAnonymous();
        Summary(summary =>
        {
            summary.Summary = "Refresh access token";
            summary.Description = "Issues a new access token. Rotates the refresh token only when it is close to expiry.";
        });
        Tags("Identity/Auth");
    }

    public override async Task HandleAsync(RefreshAccessTokenRequest req, CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var now = clock.GetCurrentInstant();
        var tokenHash = TokenService.HashToken(req.RefreshToken);

        var existingToken = await domainWriteContext.RefreshTokens
            .Include(rt => rt.User)
                .ThenInclude(u => u.OrganizationMemberships)
                    .ThenInclude(m => m.RoleAssignments)
                        .ThenInclude(assignment => assignment.Role)
            .FirstOrDefaultAsync(rt => rt.TokenHash == tokenHash, ct);

        if (existingToken is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        // A presented revoked token signals a captured refresh chain; revoke only its rotation lineage (not
        // every session, which would log the victim out on all devices).
        if (existingToken.IsRevoked)
        {
            if (!await lineageRevoker.RevokeAfterReplayAsync(existingToken.UserId, existingToken.Id, now, ct))
            {
                await SendConflictAsync(ct);
                return;
            }
            await Send.UnauthorizedAsync(ct);
            return;
        }

        if (!existingToken.IsActive(now))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var user = existingToken.User;
        var membership = user.OrganizationMemberships
            .FirstOrDefault(m => m.OrganizationId == existingToken.OrganizationId);
        if (membership is null || membership.Status != OrganizationMemberStatus.Active)
        {
            existingToken.Revoke(now);
            try
            {
                await domainWriteContext.CommitAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                await SendConflictAsync(ct);
                return;
            }
            await Send.UnauthorizedAsync(ct);
            return;
        }

        if (existingToken.AuthorizationVersion != membership.AuthorizationVersion)
        {
            existingToken.Revoke(now);
            try
            {
                await domainWriteContext.CommitAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                await SendConflictAsync(ct);
                return;
            }
            await Send.UnauthorizedAsync(ct);
            return;
        }

        await using var transaction = await domainWriteContext.BeginTransactionAsync(ct);
        await waitlistDeveloperBenefitActivator.TryActivateAsync(user, now, ct);

        var sharedRevocation = await SharedUnlockSessionRevocation.LoadAsync(domainWriteContext, existingToken, ct);
        if (sharedRevocation.IsRevoked)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }
        sharedRevocation.Fence(domainWriteContext);
        domainWriteContext.MarkPropertyAsUpdated(user, user => user.SharedUnlockSequence);
        domainWriteContext.MarkPropertyAsUpdated(existingToken, token => token.RevokedAt);
        domainWriteContext.MarkPropertyAsUpdated(membership, member => member.Status);

        var organizationAuthority = await domainWriteContext.Organizations
            .Where(o => o.Id == existingToken.OrganizationId)
            .Select(o => new
            {
                o.PlanType,
                o.OfflineAccessPolicy,
                o.OfflineAccessPolicyVersion,
            })
            .FirstAsync(ct);
        var plan = user.EffectivePlan(organizationAuthority.PlanType, now);
        var accessTokenExpiresAtCap = organizationAuthority.PlanType < PlanType.Pro
            ? user.ActiveWaitlistDeveloperBenefitEndsAt(now)
            : null;
        var permissions = membership.EffectivePermissions();
        var accessToken = tokenService.GenerateAccessToken(
            user,
            existingToken.OrganizationId,
            permissions,
            plan,
            membership.AuthorizationVersion,
            organizationAuthority.OfflineAccessPolicy,
            organizationAuthority.OfflineAccessPolicyVersion,
            accessTokenExpiresAtCap);

        var rawRefreshToken = req.RefreshToken;

        if (existingToken.IsCloseToExpiry(now, jwtOptions.Value.RefreshTokenRotationThresholdDays))
        {
            var (newRawToken, newHash) = tokenService.GenerateRefreshToken();
            var newId = guidProvider.Generate();
            var newExpiresAt = now.Plus(Duration.FromDays(jwtOptions.Value.RefreshTokenExpiryDays));

            existingToken.Revoke(now, newId);

            var newRefreshToken = RefreshToken.Create(
                newId, user.Id, existingToken.OrganizationId, newHash,
                membership.AuthorizationVersion, newExpiresAt, now,
                existingToken.SecondFactorRevision, existingToken.SecondFactorVerifiedAt,
                existingToken.SessionId ?? existingToken.Id);
            domainWriteContext.Add(newRefreshToken);

            rawRefreshToken = newRawToken;
        }

        try
        {
            await domainWriteContext.CommitAsync(transaction, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await SendConflictAsync(ct);
            return;
        }

        await Send.OkAsync(new RefreshAccessTokenResponse(
            accessToken,
            rawRefreshToken,
            user.Id,
            user.IsOnboarded,
            user.EmailVerified,
            user.ActiveWaitlistDeveloperBenefitStartedAt(now),
            user.ActiveWaitlistDeveloperBenefitEndsAt(now)), ct);
    }

    private async Task SendConflictAsync(CancellationToken ct)
    {
        AddError(ErrorResponses.General("session-refresh-conflict"));
        await Send.ErrorsAsync(StatusCodes.Status409Conflict, ct);
    }
}
