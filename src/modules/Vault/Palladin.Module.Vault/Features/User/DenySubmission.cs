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
internal sealed class DenySubmissionEndpoint(
    VaultDomainWriteContext domainWriteContext,
    IClock clock) : Endpoint<SubmissionRouteRequest, AgentSubmissionStateResponse>
{
    public override void Configure()
    {
        Post("api/submissions/{submissionId:guid}/deny");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.GrantManage);
        this.RequireEmailVerified();
        Summary(summary =>
        {
            summary.Summary = "Deny an Agent submission";
            summary.Description = "Moves a pending submission to the 30-day trash and immediately suspends the Agent's pre-approval use.";
        });
        Tags("Vault/Submissions");
    }

    public override async Task HandleAsync(SubmissionRouteRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;
        try
        {
            var decision = await SubmissionDecision.LoadAsync(
                domainWriteContext, organizationId, User.GetUserId()!.Value, req.SubmissionId, ct);
            decision.Submission.Deny(User.GetUserId()!.Value, clock.GetCurrentInstant());
            await domainWriteContext.CommitAsync(ct);
            await Send.OkAsync(AgentSubmissionIntake.ToStateResponse(decision.Submission), ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await (await SubmissionDecision.ConcurrencyRejectionAsync(domainWriteContext, organizationId, req.SubmissionId, ct))
                .SendAsync(HttpContext, ct);
        }
        catch (Exception ex) when (SubmissionRejection.Handles(ex))
        {
            await SubmissionRejection.From(ex).SendAsync(HttpContext, ct);
        }
    }
}
