using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Types;
using Palladin.Module.Agents.Infrastructure.AgentAuth;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record GetSubmissionTargetRequest
{
    public Guid VaultId { get; init; }
    public Guid EntryId { get; init; }
}

[PublicAPI]
public sealed record SubmissionTargetResponse(Guid VaultId, Guid EntryId, string EntryRevision);

// Read-only revision source for an `update` submission. Unlike credential delivery it never creates an
// access request, never consumes a grant use and emits no access event. Entry type is not returned:
// it exists only inside client-encrypted projections, so the runtime takes it from its authenticated grant.
[PublicAPI]
internal sealed class GetSubmissionTargetEndpoint(VaultDomainReadContext domainReadContext, IClock clock)
    : Endpoint<GetSubmissionTargetRequest, SubmissionTargetResponse>
{
    public override void Configure()
    {
        Get("api/agent/vaults/{vaultId:guid}/entries/{entryId:guid}/submission-target");
        AuthSchemes(AgentAuthenticationOptions.SchemeName);
        Summary(summary =>
        {
            summary.Summary = "Get the current revision of a granted Entry for an update submission";
            summary.Description = "Side-effect free. Returns the Entry's current structural revision only while the authenticated Agent holds an active grant covering it at its current access epoch; never returns field values, labels or ciphertext.";
        });
        Tags("Vault/Submissions");
    }

    public override async Task HandleAsync(GetSubmissionTargetRequest req, CancellationToken ct)
    {
        var agentId = User.GetAgentId();
        var organizationId = User.GetAgentOrganizationId();
        var accessEpoch = User.GetAgentAccessEpoch();
        if (agentId is null || organizationId is null || accessEpoch is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var agentIsCurrent = await domainReadContext.Agents.AnyAsync(x =>
            x.Id == agentId && x.OrganizationId == organizationId
            && x.Status == AgentStatus.Active && x.AccessEpoch == accessEpoch, ct);
        var revision = await domainReadContext.Entries
            .Where(x => x.OrganizationId == organizationId && x.VaultId == req.VaultId && x.Id == req.EntryId
                        && x.State == EntryState.Active)
            .Select(x => (ulong?)x.CurrentRevision.Value)
            .SingleOrDefaultAsync(ct);
        if (!agentIsCurrent || revision is null
            || !await domainReadContext.HasActiveEntryCoverageAsync(
                agentId.Value, accessEpoch.Value, req.VaultId, req.EntryId, excludingGrantId: null, ct,
                notExpiredAt: clock.GetCurrentInstant()))
        {
            await new SubmissionRejection(StatusCodes.Status403Forbidden, "update-not-granted").SendAsync(HttpContext, ct);
            return;
        }

        await Send.OkAsync(new SubmissionTargetResponse(
            req.VaultId,
            req.EntryId,
            revision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)), ct);
    }
}
