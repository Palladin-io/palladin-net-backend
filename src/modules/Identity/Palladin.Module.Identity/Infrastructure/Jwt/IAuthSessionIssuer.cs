using Palladin.Core.Security;
using Palladin.Module.Identity.Domain;
using NodaTime;

namespace Palladin.Module.Identity.Infrastructure.Jwt;

internal interface IAuthSessionIssuer
{
    // Generates the access token and a refresh token, adding the RefreshToken to the domain context.
    // The caller commits (so it can persist other changes in the same unit of work).
    (string AccessToken, string RefreshToken) Issue(
        User user,
        Organization organization,
        Permission permissions,
        uint authorizationVersion,
        Instant now,
        uint? secondFactorRevision = null,
        Instant? secondFactorVerifiedAt = null);
}
