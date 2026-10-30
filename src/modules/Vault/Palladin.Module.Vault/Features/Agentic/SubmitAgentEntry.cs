using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Module.Agents.Infrastructure.AgentAuth;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
internal sealed class SubmitAgentEntryEndpoint(
    VaultDomainWriteContext domainWriteContext,
    IClock clock) : Endpoint<AgentEntrySubmissionContract, AgentSubmissionStateResponse>
{
    public override void Configure()
    {
        Post("api/agent/submissions");
        AuthSchemes(AgentAuthenticationOptions.SchemeName);
        Summary(summary =>
        {
            summary.Summary = "Submit an encrypted Entry proposal";
            summary.Description = "Stores an Agent-signed AgentEntrySubmission after verifying its closed shape, the authenticated Agent identity and keys, the current approver key set and, for an update, an active grant over the target Entry. The payload stays opaque to the backend.";
        });
        Tags("Vault/Submissions");
    }

    public override async Task HandleAsync(AgentEntrySubmissionContract req, CancellationToken ct)
    {
        try
        {
            var agent = AuthenticatedAgentOf(User);
            var submission = AgentSubmissionCrypto.ParseSubmission(req);
            AgentSubmissionIntake.EnsureContext(submission, agent);
            if (submission.SubmissionRevision != 1)
            {
                throw new SubmissionRejection(StatusCodes.Status400BadRequest, "invalid-input");
            }

            var existing = await FindAsync(submission, ct);
            if (existing is not null)
            {
                await SendExistingAsync(existing, submission, agent, ct);
                return;
            }

            await AgentSubmissionIntake.VerifyAgentAsync(domainWriteContext, submission, agent, ct);
            await AgentSubmissionIntake.VerifyApproverKeyAsync(domainWriteContext, submission, ct);
            await AgentSubmissionIntake.VerifyUpdateGrantAsync(domainWriteContext, submission, agent, clock.GetCurrentInstant(), ct);

            var created = AgentEntrySubmission.Submit(
                submission.OrganizationId,
                submission.SubmissionId,
                submission.AgentId,
                submission.Kind,
                submission.EntryType,
                submission.Target,
                agent.AccessEpoch,
                submission.ApproverKeyVersion,
                submission.Document,
                clock.GetCurrentInstant());
            domainWriteContext.Add(created);
            try
            {
                await domainWriteContext.CommitAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException
            {
                SqlState: Palladin.Core.Persistence.PostgresErrorCodes.UniqueViolation,
                ConstraintName: "PK_AgentEntrySubmissions",
            })
            {
                domainWriteContext.Clear();
                await SendExistingAsync((await FindAsync(submission, ct))!, submission, agent, ct);
                return;
            }

            await Send.ResponseAsync(AgentSubmissionIntake.ToStateResponse(created), StatusCodes.Status201Created, ct);
        }
        catch (Exception ex) when (SubmissionRejection.Handles(ex))
        {
            await SubmissionRejection.From(ex).SendAsync(HttpContext, ct);
        }
    }

    internal static AuthenticatedAgent AuthenticatedAgentOf(System.Security.Claims.ClaimsPrincipal user) =>
        user.GetAgentId() is { } agentId
        && user.GetAgentOrganizationId() is { } organizationId
        && user.GetAgentAccessEpoch() is { } accessEpoch
            ? new AuthenticatedAgent(agentId, organizationId, accessEpoch)
            : throw new SubmissionRejection(StatusCodes.Status401Unauthorized, "unauthenticated");

    private Task<AgentEntrySubmission?> FindAsync(ParsedAgentSubmission submission, CancellationToken ct) =>
        domainWriteContext.AgentEntrySubmissions.AsNoTracking().SingleOrDefaultAsync(
            x => x.OrganizationId == submission.OrganizationId && x.Id == submission.SubmissionId, ct);

    private Task SendExistingAsync(
        AgentEntrySubmission existing,
        ParsedAgentSubmission submission,
        AuthenticatedAgent agent,
        CancellationToken ct) =>
        existing.AgentId == agent.AgentId && existing.WasSubmittedAs(submission.Document)
            ? Send.OkAsync(AgentSubmissionIntake.ToStateResponse(existing), ct)
            : new SubmissionRejection(StatusCodes.Status409Conflict, "submission-conflict").SendAsync(HttpContext, ct);
}
