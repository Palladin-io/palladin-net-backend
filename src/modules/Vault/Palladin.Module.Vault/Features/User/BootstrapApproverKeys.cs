using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Submissions;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
internal sealed class BootstrapApproverKeysEndpoint(
    VaultDomainWriteContext domainWriteContext,
    IClock clock) : Endpoint<ApproverKeySetChangeRequest, ApproverKeySetChangeResponse>
{
    public override void Configure()
    {
        Post("api/approver-keys/bootstrap");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.GrantManage);
        this.RequireEmailVerified();
        Summary(summary =>
        {
            summary.Summary = "Bootstrap the organization approver key set";
            summary.Description = "Compare-and-swap on an absent key set: stores approver key version 1, the self-signed manifest revision 1 and exactly one message-key and signing-seed wrap per current approver.";
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
            if (prepared.State.KeySet is not null)
            {
                throw new SubmissionRejection(StatusCodes.Status409Conflict, "approver-keys-exist");
            }

            var wraps = ApproverKeyContractMapper.ToDomain(
                req.Wraps, organizationId, 1, prepared.Manifest.ApproverSetRevision, prepared.State.Approvers);
            var keySet = OrganizationApproverKeySet.Bootstrap(prepared.Manifest, wraps, now);
            domainWriteContext.Add(keySet);
            await domainWriteContext.CommitAsync(ct);
            await Send.OkAsync(ApproverKeySetChange.ToResponse(keySet), ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException
        {
            SqlState: Palladin.Core.Persistence.PostgresErrorCodes.UniqueViolation,
            ConstraintName: "PK_OrganizationApproverKeySets",
        })
        {
            await new SubmissionRejection(StatusCodes.Status409Conflict, "approver-keys-exist").SendAsync(HttpContext, ct);
        }
        catch (Exception ex) when (SubmissionRejection.Handles(ex))
        {
            await SubmissionRejection.From(ex).SendAsync(HttpContext, ct);
        }
    }
}
