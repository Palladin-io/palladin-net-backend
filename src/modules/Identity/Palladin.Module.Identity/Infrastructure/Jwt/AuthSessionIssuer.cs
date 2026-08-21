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
        Guid organizationId,
        Permission permissions,
        PlanType plan,
        uint authorizationVersion,
        Instant now)
    {
        var accessToken = tokenService.GenerateAccessToken(
            user, organizationId, permissions, plan, authorizationVersion);
        var (rawRefreshToken, refreshTokenHash) = tokenService.GenerateRefreshToken();

        var expiresAt = now.Plus(Duration.FromDays(jwtOptions.Value.RefreshTokenExpiryDays));
        var refreshToken = RefreshToken.Create(
            guidProvider.Generate(), user.Id, organizationId, refreshTokenHash,
            authorizationVersion, expiresAt, now);
        domainWriteContext.Add(refreshToken);

        return (accessToken, rawRefreshToken);
    }
}
