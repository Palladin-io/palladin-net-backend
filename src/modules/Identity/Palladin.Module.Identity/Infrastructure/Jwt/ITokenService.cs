using Palladin.Core.Security;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Contracts.ValueObjects;

namespace Palladin.Module.Identity.Infrastructure.Jwt;

internal interface ITokenService
{
    string GenerateAccessToken(User user, Guid organizationId, Permission permissions, PlanType plan);
    (string rawToken, string tokenHash) GenerateRefreshToken();
}
