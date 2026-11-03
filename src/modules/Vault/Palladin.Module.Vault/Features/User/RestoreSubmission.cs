using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
internal sealed class RestoreSubmissionEndpoint(
    VaultDomainWriteContext domainWriteContext,
    IClock clock) : Endpoint<EmptyRequest, AgentSubmissionStateResponse>
{
    public override void Configure()
    {
        Post("api/submissions/{submissionId:guid}/restore");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.GrantManage);
        this.RequireEmailVerified();
        Summary(summary =>
        {
            summary.Summary = "Restore a trashed Agent submission";
            summary.Description = "Returns a trashed submission to pending for a later decision; Agent pre-approval use stays suspended.";
        });
        Tags("Vault/Submissions");
    }

    public override async Task HandleAsync(EmptyRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;
        var submissionId = Route<Guid>("submissionId");
        try
        {
            var decision = await SubmissionDecision.LoadAsync(
                domainWriteContext, organizationId, User.GetUserId()!.Value, submissionId, ct);
            decision.Submission.Restore(User.GetUserId()!.Value, clock.GetCurrentInstant());
            await domainWriteContext.CommitAsync(ct);
            await Send.OkAsync(AgentSubmissionIntake.ToStateResponse(decision.Submission), ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await (await SubmissionDecision.ConcurrencyRejectionAsync(domainWriteContext, organizationId, submissionId, ct))
                .SendAsync(HttpContext, ct);
        }
        catch (Exception ex) when (SubmissionRejection.Handles(ex))
        {
            await SubmissionRejection.From(ex).SendAsync(HttpContext, ct);
        }
    }
}
