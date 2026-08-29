using Palladin.Core.Guid;
using Palladin.Core.Security;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Module.Identity.Infrastructure.Options;
using Palladin.Module.Identity.Infrastructure.Persistence;
using JetBrains.Annotations;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Palladin.Module.Identity.Infrastructure.Jwt;

[UsedImplicitly]
internal sealed class AuthSessionIssuer(
    ITokenService tokenService,
    IdentityDomainWriteContext domainWriteContext,
    IGuidProvider guidProvider,
    IOptions<JwtOptions> jwtOptions) : IAuthSessionIssuer
{
    public (string AccessToken, string RefreshToken) Issue(
        User user,
        Organization organization,
        Permission permissions,
        uint authorizationVersion,
        Instant now)
    {
        var effectivePlan = user.EffectivePlan(organization.PlanType, now);
        var expiresAtCap = organization.PlanType < PlanType.Pro
            ? user.ActiveWaitlistDeveloperBenefitEndsAt(now)
            : null;
        var accessToken = tokenService.GenerateAccessToken(
            user,
            organization.Id,
            permissions,
            effectivePlan,
            authorizationVersion,
            organization.OfflineAccessPolicy,
            organization.OfflineAccessPolicyVersion,
            expiresAtCap);
        var (rawRefreshToken, refreshTokenHash) = tokenService.GenerateRefreshToken();

        var expiresAt = now.Plus(Duration.FromDays(jwtOptions.Value.RefreshTokenExpiryDays));
        var refreshToken = RefreshToken.Create(
            guidProvider.Generate(), user.Id, organization.Id, refreshTokenHash,
            authorizationVersion, expiresAt, now);
        domainWriteContext.Add(refreshToken);

        return (accessToken, rawRefreshToken);
    }
}
