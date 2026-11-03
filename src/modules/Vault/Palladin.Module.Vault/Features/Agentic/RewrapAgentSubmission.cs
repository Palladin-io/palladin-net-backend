using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Module.Agents.Infrastructure.AgentAuth;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Submissions;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
internal sealed class RewrapAgentSubmissionEndpoint(
    VaultDomainWriteContext domainWriteContext,
    IClock clock) : Endpoint<AgentEntrySubmissionContract, AgentSubmissionStateResponse>
{
    public override void Configure()
    {
        Put("api/agent/submissions/{submissionId:guid}/wrap");
        AuthSchemes(AgentAuthenticationOptions.SchemeName);
        Summary(summary =>
        {
            summary.Summary = "Re-wrap a pending submission for a rotated approver key";
            summary.Description = "Accepts the next submission revision only when its identity and encrypted payload are unchanged and it is addressed to the current approver key version.";
        });
        Tags("Vault/Submissions");
    }

    public override async Task HandleAsync(AgentEntrySubmissionContract req, CancellationToken ct)
    {
        try
        {
            var agent = SubmitAgentEntryEndpoint.AuthenticatedAgentOf(User);
            var submission = AgentSubmissionCrypto.ParseSubmission(req);
            if (submission.SubmissionId != Route<Guid>("submissionId"))
            {
                throw new SubmissionRejection(StatusCodes.Status400BadRequest, "invalid-input");
            }

            AgentSubmissionIntake.EnsureContext(submission, agent);
            var existing = await domainWriteContext.AgentEntrySubmissions.SingleOrDefaultAsync(
                x => x.OrganizationId == agent.OrganizationId
                     && x.Id == submission.SubmissionId
                     && x.AgentId == agent.AgentId, ct)
                ?? throw new SubmissionRejection(StatusCodes.Status404NotFound, "submission-not-found");
            if (existing.HasDocument(submission.Document))
            {
                await Send.OkAsync(AgentSubmissionIntake.ToStateResponse(existing), ct);
                return;
            }

            if (existing.State != Palladin.Core.Types.AgentSubmissionState.Pending || existing.Document is null)
            {
                throw new SubmissionRejection(StatusCodes.Status409Conflict, "submission-not-pending");
            }

            if (submission.SubmissionRevision != existing.SubmissionRevision + 1)
            {
                throw new SubmissionRejection(StatusCodes.Status409Conflict, "revision-replay");
            }

            var stored = AgentSubmissionCrypto.ParseSubmission(ApproverKeyContractMapper.ToSubmissionContract(existing.Document));
            if (!string.Equals(stored.IdentityDocument, submission.IdentityDocument, StringComparison.Ordinal)
                || !string.Equals(stored.EncodedPayload, submission.EncodedPayload, StringComparison.Ordinal))
            {
                throw new SubmissionRejection(StatusCodes.Status409Conflict, "rewrap-changed-payload");
            }

            if (submission.ApproverKeyVersion <= existing.ApproverKeyVersion)
            {
                throw new SubmissionRejection(StatusCodes.Status409Conflict, "approver-key-rotated");
            }

            await AgentSubmissionIntake.VerifyAgentAsync(domainWriteContext, submission, agent, ct);
            await AgentSubmissionIntake.VerifyApproverKeyAsync(domainWriteContext, submission, ct);
            existing.Rewrap(
                submission.SubmissionRevision,
                agent.AccessEpoch,
                submission.ApproverKeyVersion,
                submission.Document,
                clock.GetCurrentInstant());
            await domainWriteContext.CommitAsync(ct);
            await Send.OkAsync(AgentSubmissionIntake.ToStateResponse(existing), ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await new SubmissionRejection(StatusCodes.Status409Conflict, "submission-conflict").SendAsync(HttpContext, ct);
        }
        catch (Exception ex) when (SubmissionRejection.Handles(ex))
        {
            await SubmissionRejection.From(ex).SendAsync(HttpContext, ct);
        }
    }
}
