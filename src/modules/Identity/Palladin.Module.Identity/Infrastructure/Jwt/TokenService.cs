using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Palladin.Core.Guid;
using Palladin.Core.Security;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Module.Identity.Infrastructure.Options;
using JetBrains.Annotations;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NodaTime;

namespace Palladin.Module.Identity.Infrastructure.Jwt;

[UsedImplicitly]
internal sealed class TokenService(
    IOptions<JwtOptions> jwtOptions,
    IClock clock,
    IGuidProvider guidProvider) : ITokenService
{
    public string GenerateAccessToken(
        User user,
        Guid organizationId,
        Permission permissions,
        PlanType plan,
        uint authorizationVersion,
        OrganizationOfflineAccessPolicy offlineAccessPolicy,
        uint offlineAccessPolicyVersion,
        Instant? expiresAtCap = null)
    {
        if (!Enum.IsDefined(offlineAccessPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(offlineAccessPolicy));
        }

        if (offlineAccessPolicyVersion == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offlineAccessPolicyVersion));
        }

        var options = jwtOptions.Value;
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var nowInstant = clock.GetCurrentInstant();
        var now = nowInstant.ToDateTimeUtc();
        var defaultExpiresAt = nowInstant + Duration.FromMinutes(options.AccessTokenExpiryMinutes);
        var expiresAt = expiresAtCap is { } cap && cap < defaultExpiresAt
            ? cap
            : defaultExpiresAt;

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim(JwtRegisteredClaimNames.Jti, guidProvider.Generate().ToString()),
            new Claim(JwtClaimNames.OrganizationId, organizationId.ToString()),
            new Claim(JwtClaimNames.Permissions, ((int)permissions).ToString()),
            new Claim(JwtClaimNames.AuthorizationVersion, authorizationVersion.ToString()),
            new Claim(
                JwtClaimNames.OrganizationOfflineAccessPolicy,
                ((ushort)offlineAccessPolicy).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new Claim(
                JwtClaimNames.OrganizationOfflineAccessPolicyVersion,
                offlineAccessPolicyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new Claim(JwtClaimNames.Plan, plan.ToString()),
            new Claim(JwtClaimNames.DisplayName, user.DisplayName),
            new Claim(JwtClaimNames.EmailVerified, user.EmailVerified.ToString().ToLowerInvariant()),
            new Claim("is_onboarded", user.IsOnboarded.ToString().ToLowerInvariant()),
        };

        var token = new JwtSecurityToken(
            issuer: options.Issuer,
            audience: options.Audience,
            claims: claims,
            notBefore: now,
            expires: expiresAt.ToDateTimeUtc(),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public (string rawToken, string tokenHash) GenerateRefreshToken()
    {
        var randomBytes = RandomNumberGenerator.GetBytes(32);
        var rawToken = Convert.ToBase64String(randomBytes);
        var tokenHash = HashToken(rawToken);

        return (rawToken, tokenHash);
    }

    internal static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToBase64String(bytes);
    }
}
