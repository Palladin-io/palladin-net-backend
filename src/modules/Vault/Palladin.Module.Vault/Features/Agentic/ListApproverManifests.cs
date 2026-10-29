using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Palladin.Core.Types;
using Palladin.Module.Agents.Infrastructure.AgentAuth;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Submissions;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record ListApproverManifestsRequest
{
    [QueryParam]
    public string? AfterRevision { get; init; }
}

[PublicAPI]
public sealed record ListApproverManifestsResponse(
    Guid OrganizationId,
    uint AgentKeyVersion,
    string AgentAccessEpoch,
    IReadOnlyList<OrganizationApproverManifestContract> Manifests);

[PublicAPI]
internal sealed class ListApproverManifestsEndpoint(VaultDomainReadContext domainReadContext)
    : Endpoint<ListApproverManifestsRequest, ListApproverManifestsResponse>
{
    private const int MaximumManifests = 1000;

    public override void Configure()
    {
        Get("api/agent/approver-manifests");
        AuthSchemes(AgentAuthenticationOptions.SchemeName);
        Summary(summary =>
        {
            summary.Summary = "List the organization's signed approver manifests";
            summary.Description = "Returns the authenticated Agent's current organization, X25519 key version and access epoch required by submissions, plus append-only approver manifests after the given revision in ascending order so the Agent runtime can verify the self-signed and continuity-signed chain against its pinned key.";
        });
        Tags("Vault/Submissions");
    }

    public override async Task HandleAsync(ListApproverManifestsRequest req, CancellationToken ct)
    {
        var agentId = User.GetAgentId();
        var organizationId = User.GetAgentOrganizationId();
        var accessEpoch = User.GetAgentAccessEpoch();
        if (agentId is null || organizationId is null || accessEpoch is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var agent = await domainReadContext.Agents
            .Where(x => x.Id == agentId && x.OrganizationId == organizationId
                        && x.Status == AgentStatus.Active && x.AccessEpoch == accessEpoch)
            .Select(x => new { x.RecipientKeyVersion, x.AccessEpoch })
            .SingleOrDefaultAsync(ct);
        if (agent is null)
        {
            await new SubmissionRejection(StatusCodes.Status403Forbidden, "agent-not-active").SendAsync(HttpContext, ct);
            return;
        }

        ulong afterRevision;
        try
        {
            afterRevision = req.AfterRevision is null
                ? 0
                : AgentSubmissionCrypto.ParseUInt64(req.AfterRevision, allowZero: true);
        }
        catch (AgentSubmissionInputException ex)
        {
            await SubmissionRejection.From(ex).SendAsync(HttpContext, ct);
            return;
        }

        var hasKeySet = await domainReadContext.OrganizationApproverKeySets
            .AnyAsync(x => x.OrganizationId == organizationId, ct);
        if (!hasKeySet)
        {
            await new SubmissionRejection(StatusCodes.Status404NotFound, "no-approvers").SendAsync(HttpContext, ct);
            return;
        }

        var documents = await domainReadContext.OrganizationApproverManifests
            .Where(x => x.OrganizationId == organizationId && x.ManifestRevision > afterRevision)
            .OrderBy(x => x.ManifestRevision)
            .Take(MaximumManifests)
            .Select(x => x.Document)
            .ToListAsync(ct);
        await Send.OkAsync(new ListApproverManifestsResponse(
            organizationId.Value,
            agent.RecipientKeyVersion,
            agent.AccessEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
            documents.Select(ApproverKeyContractMapper.ToManifestContract).ToList()), ct);
    }
}
