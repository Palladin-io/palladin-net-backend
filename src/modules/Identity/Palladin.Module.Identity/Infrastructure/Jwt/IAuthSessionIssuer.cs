using Palladin.Core.Security;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Contracts.ValueObjects;
using NodaTime;

namespace Palladin.Module.Identity.Infrastructure.Jwt;

internal interface IAuthSessionIssuer
{
    // Generates the access token and a refresh token, adding the RefreshToken to the domain context.
    // The caller commits (so it can persist other changes in the same unit of work).
    (string AccessToken, string RefreshToken) Issue(
        User user,
        Guid organizationId,
        Permission permissions,
        PlanType plan,
        uint authorizationVersion,
        Instant now);
}
