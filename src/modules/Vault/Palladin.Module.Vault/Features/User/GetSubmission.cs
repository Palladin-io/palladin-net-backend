using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Palladin.Core.Security;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Submissions;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record GetSubmissionResponse(
    AgentEntrySubmissionContract Submission,
    string AgentEd25519PublicKey,
    uint AgentEd25519KeyVersion);

[PublicAPI]
internal sealed class GetSubmissionEndpoint(VaultDomainReadContext domainReadContext)
    : Endpoint<SubmissionRouteRequest, GetSubmissionResponse>
{
    public override void Configure()
    {
        Get("api/submissions/{submissionId:guid}");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.GrantManage);
        this.RequireEmailVerified();
        Summary(summary =>
        {
            summary.Summary = "Get an Agent submission for review";
            summary.Description = "Returns the signed encrypted submission to a current approver together with the submitting Agent's authoritative Ed25519 key, so the client verifies the Agent signature before opening the DEK.";
        });
        Tags("Vault/Submissions");
    }

    public override async Task HandleAsync(SubmissionRouteRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;
        var state = await domainReadContext.LoadApproverStateAsync(organizationId, ct);
        if (!state.IsServed(User.GetUserId()!.Value))
        {
            await new SubmissionRejection(StatusCodes.Status403Forbidden, "approver-not-current").SendAsync(HttpContext, ct);
            return;
        }

        var row = await domainReadContext.AgentEntrySubmissions
            .Where(x => x.OrganizationId == organizationId && x.Id == req.SubmissionId && x.Document != null)
            .Select(x => new
            {
                x.Document,
                Agent = domainReadContext.Agents
                    .Where(agent => agent.Id == x.AgentId && agent.OrganizationId == organizationId)
                    .Select(agent => new { agent.SigningPublicKey, agent.RecipientKeyVersion })
                    .FirstOrDefault(),
            })
            .SingleOrDefaultAsync(ct);
        if (row?.Agent is null)
        {
            await new SubmissionRejection(StatusCodes.Status404NotFound, "submission-not-available").SendAsync(HttpContext, ct);
            return;
        }

        await Send.OkAsync(new GetSubmissionResponse(
            ApproverKeyContractMapper.ToSubmissionContract(row.Document!),
            AgentSubmissionCrypto.Encode(Convert.FromBase64String(row.Agent.SigningPublicKey)),
            row.Agent.RecipientKeyVersion), ct);
    }
}
