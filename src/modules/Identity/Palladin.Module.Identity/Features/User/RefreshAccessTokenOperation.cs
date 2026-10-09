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

using Palladin.Module.Identity.Shared;

namespace Palladin.Module.Identity.Features;

internal sealed class RefreshAccessTokenOperation(
    IdentityDomainWriteContext domainWriteContext,
    ITokenService tokenService,
    RefreshTokenLineageRevoker lineageRevoker,
    IGuidProvider guidProvider,
    IClock clock,
    WaitlistDeveloperBenefitActivator waitlistDeveloperBenefitActivator,
    IOptions<JwtOptions> jwtOptions)
{
    public async Task<IdentityOperationResult<RefreshAccessTokenResponse>> ExecuteAsync(RefreshAccessTokenRequest req, CancellationToken ct, bool forceRotation = false)
    {
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
            return IdentityOperationResult<RefreshAccessTokenResponse>.Status(401);
        }

        // A presented revoked token signals a captured refresh chain; revoke only its rotation lineage (not
        // every session, which would log the victim out on all devices).
        if (existingToken.IsRevoked)
        {
            if (!await lineageRevoker.RevokeAfterReplayAsync(existingToken.UserId, existingToken.Id, now, ct))
            {
                return Conflict();
            }
            return IdentityOperationResult<RefreshAccessTokenResponse>.Status(401);
        }

        if (!existingToken.IsActive(now))
        {
            return IdentityOperationResult<RefreshAccessTokenResponse>.Status(401);
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
                return Conflict();
            }
            return IdentityOperationResult<RefreshAccessTokenResponse>.Status(401);
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
                return Conflict();
            }
            return IdentityOperationResult<RefreshAccessTokenResponse>.Status(401);
        }

        await using var transaction = await domainWriteContext.BeginTransactionAsync(ct);
        await waitlistDeveloperBenefitActivator.TryActivateAsync(user, now, ct);

        var sharedRevocation = await SharedUnlockSessionRevocation.LoadAsync(domainWriteContext, existingToken, ct);
        if (sharedRevocation.IsRevoked)
        {
            return IdentityOperationResult<RefreshAccessTokenResponse>.Status(401);
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

        if (forceRotation || existingToken.IsCloseToExpiry(now, jwtOptions.Value.RefreshTokenRotationThresholdDays))
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
            return Conflict();
        }

        return IdentityOperationResult<RefreshAccessTokenResponse>.Ok(new RefreshAccessTokenResponse(
            accessToken,
            rawRefreshToken,
            user.Id,
            user.IsOnboarded,
            user.EmailVerified,
            user.ActiveWaitlistDeveloperBenefitStartedAt(now),
            user.ActiveWaitlistDeveloperBenefitEndsAt(now)));
    }

    private static IdentityOperationResult<RefreshAccessTokenResponse> Conflict() =>
        IdentityOperationResult<RefreshAccessTokenResponse>.Failure(409, ErrorResponses.General("session-refresh-conflict"));
}
