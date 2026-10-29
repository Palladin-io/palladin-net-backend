using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Submissions;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
internal sealed class AddApproversEndpoint(
    VaultDomainWriteContext domainWriteContext,
    IClock clock) : Endpoint<ApproverKeySetChangeRequest, ApproverKeySetChangeResponse>
{
    public override void Configure()
    {
        Post("api/approver-keys/add-approvers");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.GrantManage);
        this.RequireEmailVerified();
        Summary(summary =>
        {
            summary.Summary = "Wrap the current approver keys for new approvers";
            summary.Description = "A current approver wraps the unchanged approver key version for approvers that hold no wrap yet and signs the next manifest revision with the current signing key. Not allowed while a removed or re-keyed approver requires rotation.";
        });
        Tags("Vault/Submissions");
    }

    public override async Task HandleAsync(ApproverKeySetChangeRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;
        var userId = User.GetUserId()!.Value;
        var now = clock.GetCurrentInstant();
        try
        {
            var prepared = await ApproverKeySetChange.PrepareAsync(domainWriteContext, req, organizationId, userId, now, ct);
            var state = prepared.State;
            var keySet = state.KeySet
                ?? throw new SubmissionRejection(StatusCodes.Status404NotFound, "no-approvers");
            if (!state.IsServed(userId))
            {
                throw new SubmissionRejection(StatusCodes.Status403Forbidden, "approver-not-current");
            }

            if (state.RotationRequired)
            {
                throw new SubmissionRejection(StatusCodes.Status409Conflict, "approver-rotation-required");
            }

            var newApprovers = state.Approvers.Where(x => !state.HasCurrentWrap(x.MemberId)).ToList();
            if (newApprovers.Count == 0)
            {
                throw new SubmissionRejection(StatusCodes.Status409Conflict, "no-new-approvers");
            }

            var wraps = ApproverKeyContractMapper.ToDomain(
                req.Wraps, organizationId, keySet.KeyVersion, prepared.Manifest.ApproverSetRevision, newApprovers);
            keySet.AddApprovers(prepared.Manifest, wraps, now);
            await domainWriteContext.CommitAsync(ct);
            await Send.OkAsync(ApproverKeySetChange.ToResponse(keySet), ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await new SubmissionRejection(StatusCodes.Status409Conflict, "approver-set-changed").SendAsync(HttpContext, ct);
        }
        catch (Exception ex) when (SubmissionRejection.Handles(ex))
        {
            await SubmissionRejection.From(ex).SendAsync(HttpContext, ct);
        }
    }
}
