using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace Palladin.Core.Security;

public static class ClaimsPrincipalExtensions
{
    public static Guid? GetUserId(this ClaimsPrincipal principal)
    {
        var raw = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                  ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out var parsed) ? parsed : null;
    }

    public static Guid? GetOrganizationId(this ClaimsPrincipal principal)
    {
        var raw = principal.FindFirst(JwtClaimNames.OrganizationId)?.Value;
        return Guid.TryParse(raw, out var parsed) ? parsed : null;
    }

    public static string GetDisplayName(this ClaimsPrincipal principal) =>
        principal.FindFirst(JwtClaimNames.DisplayName)?.Value ?? string.Empty;

    public static Permission GetPermissions(this ClaimsPrincipal principal)
    {
        var raw = principal.FindFirst(JwtClaimNames.Permissions)?.Value;
        return int.TryParse(raw, out var value) ? (Permission)value : Permission.None;
    }

    public static uint? GetAuthorizationVersion(this ClaimsPrincipal principal)
    {
        var raw = principal.FindFirst(JwtClaimNames.AuthorizationVersion)?.Value;
        return uint.TryParse(raw, out var value) && value > 0 ? value : null;
    }

    public static bool GetEmailVerified(this ClaimsPrincipal principal) =>
        bool.TryParse(principal.FindFirst(JwtClaimNames.EmailVerified)?.Value, out var value) && value;
}
