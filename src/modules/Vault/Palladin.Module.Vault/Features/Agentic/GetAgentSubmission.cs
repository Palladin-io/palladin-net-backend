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
public sealed record GetAgentSubmissionRequest
{
    public Guid SubmissionId { get; init; }
}

[PublicAPI]
internal sealed class GetAgentSubmissionEndpoint(
    VaultDomainReadContext domainReadContext,
    IEnumerable<IEventPublisher> eventPublishers,
    IClock clock) : Endpoint<GetAgentSubmissionRequest, AgentEntrySubmissionContract>
{
    public override void Configure()
    {
        Get("api/agent/submissions/{submissionId:guid}");
        AuthSchemes(AgentAuthenticationOptions.SchemeName);
        Summary(summary =>
        {
            summary.Summary = "Fetch a pending submission for pre-approval Inject";
            summary.Description = "Only the authoring active Agent with its current access epoch may fetch a pending, non-suspended submission; every fetch is audited without secret material.";
        });
        Tags("Vault/Submissions");
    }

    public override async Task HandleAsync(GetAgentSubmissionRequest req, CancellationToken ct)
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
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == req.SubmissionId, ct);
        var agentIsCurrent = await domainReadContext.Agents.AnyAsync(x =>
            x.Id == agentId && x.OrganizationId == organizationId
            && x.Status == AgentStatus.Active && x.AccessEpoch == accessEpoch, ct);
        if (submission is null || !agentIsCurrent || !submission.IsUsableBy(agentId.Value, accessEpoch.Value))
        {
            await new SubmissionRejection(StatusCodes.Status403Forbidden, "submission-not-usable").SendAsync(HttpContext, ct);
            return;
        }

        await eventPublishers.PublishAsync(submission.UseEvent(clock.GetCurrentInstant()), ct);
        await Send.OkAsync(ApproverKeyContractMapper.ToSubmissionContract(submission.Document!), ct);
    }
}
