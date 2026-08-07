using System.Globalization;
using System.Security.Claims;
using JetBrains.Annotations;

namespace Palladin.Module.Agents.Infrastructure.AgentAuth;

[PublicAPI]
public static class AgentClaimsPrincipalExtensions
{
    public static Guid? GetAgentId(this ClaimsPrincipal principal) =>
        ParseGuid(principal.FindFirst(AgentClaimNames.AgentId)?.Value);

    public static Guid? GetAgentOrganizationId(this ClaimsPrincipal principal) =>
        ParseGuid(principal.FindFirst(AgentClaimNames.OrganizationId)?.Value);

    public static uint? GetAgentAccessEpoch(this ClaimsPrincipal principal) =>
        uint.TryParse(
            principal.FindFirst(AgentClaimNames.AccessEpoch)?.Value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : null;

    private static Guid? ParseGuid(string? raw) =>
        Guid.TryParse(raw, out var parsed) ? parsed : null;
}
