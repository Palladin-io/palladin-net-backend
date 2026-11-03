using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Events;
using Palladin.Core.Types;
using Palladin.Module.Agents.Infrastructure.AgentAuth;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Submissions;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record GetRewrapSourceRequest
{
    public Guid SubmissionId { get; init; }
}

// Separate authority from pre-approval use (contract preApprovalUse.reWrapSource): the authoring Agent at the
// submission's access epoch may fetch a pending submission even while use is suspended, but only while its
// stored approver key version is behind the current one. Serving it is audited and never counts as use.
[PublicAPI]
internal sealed class GetRewrapSourceEndpoint(
    VaultDomainReadContext domainReadContext,
    IEnumerable<IEventPublisher> eventPublishers,
    IClock clock) : Endpoint<GetRewrapSourceRequest, AgentEntrySubmissionContract>
{
    public override void Configure()
    {
        Get("api/agent/submissions/{submissionId:guid}/rewrap-source");
        AuthSchemes(AgentAuthenticationOptions.SchemeName);
        Summary(summary =>
        {
            summary.Summary = "Fetch a pending submission to re-wrap it for a newer approver key";
            summary.Description = "Only for the authoring active Agent at the submission's access epoch, while the submission is pending (suspended or not) and was wrapped for an older approver key version. Never authorizes Inject.";
        });
        Tags("Vault/Submissions");
    }

    public override async Task HandleAsync(GetRewrapSourceRequest req, CancellationToken ct)
    {
        var agentId = User.GetAgentId();
        var organizationId = User.GetAgentOrganizationId();
        var accessEpoch = User.GetAgentAccessEpoch();
        if (agentId is null || organizationId is null || accessEpoch is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var submission = await domainReadContext.AgentEntrySubmissions
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == req.SubmissionId
                                       && x.AgentId == agentId, ct);
        if (submission is null)
        {
            await new SubmissionRejection(StatusCodes.Status404NotFound, "submission-not-found").SendAsync(HttpContext, ct);
            return;
        }

        var agentIsCurrent = await domainReadContext.Agents.AnyAsync(x =>
            x.Id == agentId && x.OrganizationId == organizationId
            && x.Status == AgentStatus.Active && x.AccessEpoch == accessEpoch, ct);
        if (!agentIsCurrent || !submission.IsRewrappableBy(accessEpoch.Value))
        {
            await new SubmissionRejection(StatusCodes.Status403Forbidden, "submission-not-rewrappable").SendAsync(HttpContext, ct);
            return;
        }

        var currentKeyVersion = await domainReadContext.OrganizationApproverKeySets
            .Where(x => x.OrganizationId == organizationId)
            .Select(x => (uint?)x.KeyVersion)
            .SingleOrDefaultAsync(ct);
        if (currentKeyVersion is null || submission.ApproverKeyVersion >= currentKeyVersion)
        {
            await new SubmissionRejection(StatusCodes.Status409Conflict, "rewrap-not-required").SendAsync(HttpContext, ct);
            return;
        }

        await eventPublishers.PublishAsync(submission.RewrapSourceEvent(clock.GetCurrentInstant()), ct);
        await Send.OkAsync(ApproverKeyContractMapper.ToSubmissionContract(submission.Document!), ct);
    }
}
