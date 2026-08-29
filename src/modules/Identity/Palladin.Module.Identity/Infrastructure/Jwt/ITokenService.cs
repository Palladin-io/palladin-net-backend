using Palladin.Core.Security;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Contracts.ValueObjects;
using NodaTime;

namespace Palladin.Module.Identity.Infrastructure.Jwt;

internal interface ITokenService
{
    string GenerateAccessToken(
        User user,
        Guid organizationId,
        Permission permissions,
        PlanType plan,
        uint authorizationVersion,
        OrganizationOfflineAccessPolicy offlineAccessPolicy,
        uint offlineAccessPolicyVersion,
        Instant? expiresAtCap = null);
    (string rawToken, string tokenHash) GenerateRefreshToken();
}
