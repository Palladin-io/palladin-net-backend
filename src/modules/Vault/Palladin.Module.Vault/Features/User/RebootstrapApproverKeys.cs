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
internal sealed class RebootstrapApproverKeysEndpoint(
    VaultDomainWriteContext domainWriteContext,
    IClock clock) : Endpoint<ApproverKeySetChangeRequest, ApproverKeySetChangeResponse>
{
    public override void Configure()
    {
        Post("api/approver-keys/rebootstrap");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.GrantManage);
        this.RequireEmailVerified();
        Summary(summary =>
        {
            summary.Summary = "Re-bootstrap an unavailable approver key set";
            summary.Description = "Only while no current approver holds a wrap for their current Member key: a current approver introduces the next key version without continuity, wrapped for every current approver, and atomically replaces every previous wrap. Agents must explicitly re-trust the new chain.";
        });
        Tags("Vault/Submissions");
    }

    public override async Task HandleAsync(ApproverKeySetChangeRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;
        var now = clock.GetCurrentInstant();
        try
        {
            ApproverKeySetChange.EnsureNoContinuity(req);
            var prepared = await ApproverKeySetChange.PrepareAsync(
                domainWriteContext, req, organizationId, User.GetUserId()!.Value, now, ct);
            var state = prepared.State;
            var keySet = state.KeySet
                ?? throw new SubmissionRejection(StatusCodes.Status404NotFound, "no-approvers");
            if (state.HasServedApprover)
            {
                throw new SubmissionRejection(StatusCodes.Status409Conflict, "approver-keys-available");
            }

            var wraps = ApproverKeyContractMapper.ToDomain(
                req.Wraps, organizationId, prepared.Manifest.KeyVersion, prepared.Manifest.ApproverSetRevision,
                state.Approvers);
            keySet.Rebootstrap(prepared.Manifest, wraps, now);
            await domainWriteContext.CommitAsync(ct);
            await Send.OkAsync(ApproverKeySetChange.ToResponse(keySet), ct);
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException
            || ex.InnerException is Npgsql.PostgresException
            {
                SqlState: Palladin.Core.Persistence.PostgresErrorCodes.UniqueViolation,
            })
        {
            await new SubmissionRejection(StatusCodes.Status409Conflict, "approver-set-changed").SendAsync(HttpContext, ct);
        }
        catch (Exception ex) when (SubmissionRejection.Handles(ex))
        {
            await SubmissionRejection.From(ex).SendAsync(HttpContext, ct);
        }
    }
}
