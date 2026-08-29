using System.Globalization;
using System.Security.Claims;
using Palladin.Core.Security;

namespace Palladin.Module.Identity.Contracts.ValueObjects;

public enum OrganizationOfflineAccessPolicy : ushort
{
    Disabled = 0,
    OneHour = 1,
    FourHours = 2,
    TwentyFourHours = 3,
}

public sealed record OrganizationOfflineAccessAuthority(
    uint OrganizationMembershipGeneration,
    OrganizationOfflineAccessPolicy OfflineAccessPolicy,
    uint OfflineAccessPolicyVersion)
{
    public static OrganizationOfflineAccessAuthority? FromAuthenticatedPrincipal(
        ClaimsPrincipal principal)
    {
        var organizationMembershipGeneration = principal.GetAuthorizationVersion();
        var rawPolicy = principal.FindFirst(JwtClaimNames.OrganizationOfflineAccessPolicy)?.Value;
        var rawPolicyVersion = principal.FindFirst(JwtClaimNames.OrganizationOfflineAccessPolicyVersion)?.Value;
        if (organizationMembershipGeneration is null
            || !ushort.TryParse(rawPolicy, NumberStyles.None, CultureInfo.InvariantCulture, out var policyCode)
            || !Enum.IsDefined(typeof(OrganizationOfflineAccessPolicy), policyCode)
            || !uint.TryParse(
                rawPolicyVersion,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var policyVersion)
            || policyVersion == 0)
        {
            return null;
        }

        return new OrganizationOfflineAccessAuthority(
            organizationMembershipGeneration.Value,
            (OrganizationOfflineAccessPolicy)policyCode,
            policyVersion);
    }
}
