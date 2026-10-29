using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Palladin.Core.Security;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Submissions;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record GetApproverKeysResponse(
    string Status,
    uint? KeyVersion,
    string? ApproverSetRevision,
    OrganizationApproverManifestContract? Manifest,
    IReadOnlyList<ApproverKeyWrapContract>? MyWraps,
    IReadOnlyList<ApproverContract> Approvers,
    string ApproverSetDigest,
    bool RotationRequired);

[PublicAPI]
internal sealed class GetApproverKeysEndpoint(VaultDomainReadContext domainReadContext)
    : Endpoint<EmptyRequest, GetApproverKeysResponse>
{
    internal const string Absent = "absent";
    internal const string Ready = "ready";
    internal const string Unavailable = "unavailable";

    public override void Configure()
    {
        Get("api/approver-keys");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.GrantManage);
        this.RequireEmailVerified();
        Summary(summary =>
        {
            summary.Summary = "Get the organization approver key set";
            summary.Description = "Returns the current signed approver manifest, the caller's own approver key wraps only while they address the caller's current Member key, and the authoritative approver set with its digest.";
        });
        Tags("Vault/Submissions");
    }

    public override async Task HandleAsync(EmptyRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;
        var userId = User.GetUserId()!.Value;
        var state = await domainReadContext.LoadApproverStateAsync(organizationId, ct);
        var keySet = state.KeySet;
        OrganizationApproverManifestContract? manifest = null;
        if (keySet is not null)
        {
            var document = await domainReadContext.OrganizationApproverManifests
                .Where(x => x.OrganizationId == organizationId && x.ManifestRevision == keySet.ManifestRevision)
                .Select(x => x.Document)
                .SingleAsync(ct);
            manifest = ApproverKeyContractMapper.ToManifestContract(document);
        }

        await Send.OkAsync(new GetApproverKeysResponse(
            keySet is null ? Absent : state.HasServedApprover ? Ready : Unavailable,
            keySet?.KeyVersion,
            keySet?.ApproverSetRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            manifest,
            keySet is null ? null : state.ServedWrapsOf(userId).Select(ApproverKeyContractMapper.ToContract).ToList(),
            state.Approvers.Select(x => ApproverKeyContractMapper.ToContract(x, state.IsServed(x.MemberId))).ToList(),
            AgentSubmissionCrypto.Encode(state.Digest),
            state.RotationRequired), ct);
    }
}
