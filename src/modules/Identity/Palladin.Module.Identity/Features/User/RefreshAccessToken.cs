using Palladin.Core.Guid;
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
            await RevokeTokenLineageAsync(existingToken, now, ct);
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
            await domainWriteContext.CommitAsync(ct);
            await Send.UnauthorizedAsync(ct);
            return;
        }

        if (existingToken.AuthorizationVersion != membership.AuthorizationVersion)
        {
            existingToken.Revoke(now);
            await domainWriteContext.CommitAsync(ct);
            await Send.UnauthorizedAsync(ct);
            return;
        }

        await using var transaction = await domainWriteContext.BeginTransactionAsync(ct);
        await waitlistDeveloperBenefitActivator.TryActivateAsync(user, now, ct);

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
            domainWriteContext.Update(existingToken);

            var newRefreshToken = RefreshToken.Create(
                newId, user.Id, existingToken.OrganizationId, newHash,
                membership.AuthorizationVersion, newExpiresAt, now);
            domainWriteContext.Add(newRefreshToken);

            rawRefreshToken = newRawToken;
        }

        await domainWriteContext.CommitAsync(transaction, ct);

        await Send.OkAsync(new RefreshAccessTokenResponse(
            accessToken,
            rawRefreshToken,
            user.Id,
            user.IsOnboarded,
            user.EmailVerified,
            user.ActiveWaitlistDeveloperBenefitStartedAt(now),
            user.ActiveWaitlistDeveloperBenefitEndsAt(now)), ct);
    }

    // Walk the presented token's rotation lineage via the ReplacedByTokenId chain and revoke the still-active
    // descendants it was rotated into. Ancestors are already revoked (a token is only revoked once it is
    // replaced), so following the chain forward from the replayed token is enough to kill the live session it
    // belongs to without touching unrelated sessions on the user's other devices.
    private async Task RevokeTokenLineageAsync(RefreshToken presentedToken, Instant now, CancellationToken ct)
    {
        var visited = new HashSet<Guid> { presentedToken.Id };
        var nextId = presentedToken.ReplacedByTokenId;
        while (nextId is { } id && visited.Add(id))
        {
            var token = await domainWriteContext.RefreshTokens.FirstOrDefaultAsync(rt => rt.Id == id, ct);
            if (token is null)
            {
                break;
            }

            if (token.RevokedAt is null)
            {
                token.Revoke(now);
            }

            nextId = token.ReplacedByTokenId;
        }

        await domainWriteContext.CommitAsync(ct);
    }
}
