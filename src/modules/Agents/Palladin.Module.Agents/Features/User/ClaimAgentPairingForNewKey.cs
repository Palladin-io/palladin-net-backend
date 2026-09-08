using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Palladin.Core.Security;
using Palladin.Module.Agents.Shared;

namespace Palladin.Module.Agents.Features;

[PublicAPI]
internal sealed class ClaimAgentPairingForNewKeyEndpoint(
    AgentPairingClaims claims) : Endpoint<ClaimAgentPairingRequest, ClaimAgentPairingResponse>
{
    public override void Configure()
    {
        Post("api/agent-pairings/{PairingId}/claim-for-new-key");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.AgentManage | Permission.WriteApiKey);
        this.RequireEmailVerified();
        Tags("Agents/Pairing");
        Summary(summary =>
        {
            summary.Summary = "Claim an Agent pairing request";
            summary.Description = "Binds the opaque request to the authenticated organization and returns safe approval metadata.";
        });
    }

    public override async Task HandleAsync(ClaimAgentPairingRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId();
        if (organizationId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var pairing = await claims.ClaimAsync(req.PairingId, organizationId.Value, ct);
        if (pairing is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        await Send.OkAsync(new ClaimAgentPairingResponse(
            pairing.Id,
            pairing.DisplayName,
            pairing.ReservedDisplayName,
            pairing.Type,
            AgentPublicKey.Hint(pairing.PublicKey),
            pairing.ExpiresAt,
            true,
            [],
            pairing.Hostname,
            pairing.Ip), ct);
    }
}
