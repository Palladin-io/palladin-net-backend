using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Module.Agents.Infrastructure.Persistence;

namespace Palladin.Module.Agents.Features;

[PublicAPI]
public sealed record RejectAgentPairingRequest
{
    public Guid PairingId { get; init; }
}

[PublicAPI]
internal sealed class RejectAgentPairingEndpoint(
    AgentsDomainWriteContext domainWriteContext,
    IClock clock) : Endpoint<RejectAgentPairingRequest>
{
    public override void Configure()
    {
        Post("api/agent-pairings/{PairingId}/reject");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.AgentManage);
        this.RequireEmailVerified();
        Tags("Agents/Pairing");
        Summary(summary =>
        {
            summary.Summary = "Reject an Agent browser pairing";
            summary.Description = "Terminates a claimed pending request without creating an Agent or credential.";
        });
    }

    public override async Task HandleAsync(RejectAgentPairingRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId();
        if (organizationId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var pairing = await domainWriteContext.AgentPairingRequests
            .FirstOrDefaultAsync(x => x.Id == req.PairingId, ct);
        if (pairing is null || !pairing.TryReject(organizationId.Value, clock.GetCurrentInstant()))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        try
        {
            await domainWriteContext.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            domainWriteContext.Clear();
            await Send.StatusCodeAsync(StatusCodes.Status409Conflict, ct);
            return;
        }
        await Send.NoContentAsync(ct);
    }
}
