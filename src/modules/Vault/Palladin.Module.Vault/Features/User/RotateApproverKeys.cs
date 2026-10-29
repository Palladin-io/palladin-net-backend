using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Submissions;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
internal sealed class RotateApproverKeysEndpoint(
    VaultDomainWriteContext domainWriteContext,
    IClock clock) : Endpoint<ApproverKeySetChangeRequest, ApproverKeySetChangeResponse>
{
    public override void Configure()
    {
        Post("api/approver-keys/rotate");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.GrantManage);
        this.RequireEmailVerified();
        Summary(summary =>
        {
            summary.Summary = "Rotate the organization approver keys";
            summary.Description = "Atomically commits the next approver key version, its manifest with a continuity signature by the current signing key, new wraps for every current approver and the removal of every previous-version wrap.";
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

            if (!AgentSubmissionCrypto.VerifyContinuity(req.Manifest, keySet.SigningPublicKey))
            {
                throw new SubmissionRejection(StatusCodes.Status400BadRequest, "manifest-continuity-invalid");
            }

            var wraps = ApproverKeyContractMapper.ToDomain(
                req.Wraps, organizationId, prepared.Manifest.KeyVersion, prepared.Manifest.ApproverSetRevision,
                state.Approvers);
            keySet.Rotate(prepared.Manifest, wraps, now);
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
