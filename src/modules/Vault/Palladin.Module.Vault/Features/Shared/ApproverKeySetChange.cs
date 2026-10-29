using JetBrains.Annotations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record ApproverKeySetChangeRequest(
    Guid ChallengeId,
    OrganizationApproverManifestContract Manifest,
    IReadOnlyList<ApproverKeyWrapContract> Wraps);

[PublicAPI]
public sealed record ApproverKeySetChangeResponse(
    uint KeyVersion,
    string ManifestRevision,
    string ApproverSetRevision);

internal sealed record PreparedApproverKeySetChange(ApproverKeySetState State, ApproverManifest Manifest);

// Shared fence for bootstrap, add-approvers and rotation: a live single-use challenge of the caller,
// an unchanged authoritative approver set and a self-signed manifest bound to exactly that challenge.
internal static class ApproverKeySetChange
{
    internal static async Task<PreparedApproverKeySetChange> PrepareAsync(
        VaultDomainWriteContext domainWriteContext,
        ApproverKeySetChangeRequest req,
        Guid organizationId,
        Guid userId,
        Instant now,
        CancellationToken ct)
    {
        var challenge = await domainWriteContext.ApproverKeyChallenges
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.IssuedTo == userId, ct);
        if (challenge is null || !challenge.IsUsable(req.ChallengeId, now))
        {
            throw new SubmissionRejection(StatusCodes.Status409Conflict, "challenge-invalid");
        }

        var state = await domainWriteContext.LoadApproverStateAsync(organizationId, ct);
        if (!state.TryGet(userId, out _))
        {
            throw new SubmissionRejection(StatusCodes.Status403Forbidden, "not-an-approver");
        }

        if (!state.Digest.AsSpan().SequenceEqual(challenge.ApproverSetDigest)
            || challenge.ApproverSetRevision != (state.KeySet?.ApproverSetRevision ?? 0) + 1)
        {
            throw new SubmissionRejection(StatusCodes.Status409Conflict, "approver-set-changed");
        }

        var manifest = AgentSubmissionCrypto.ParseManifest(req.Manifest);
        if (manifest.OrganizationId != organizationId
            || req.Manifest.IssuedAt != AgentSubmissionCrypto.FormatIssuedAt(challenge.IssuedAt)
            || manifest.ApproverSetRevision != challenge.ApproverSetRevision
            || !manifest.ApproverSetDigest.AsSpan().SequenceEqual(challenge.ApproverSetDigest))
        {
            throw new SubmissionRejection(StatusCodes.Status400BadRequest, "manifest-binding-mismatch");
        }

        domainWriteContext.Remove(challenge);
        return new PreparedApproverKeySetChange(state, manifest);
    }

    internal static ApproverKeySetChangeResponse ToResponse(OrganizationApproverKeySet keySet) => new(
        keySet.KeyVersion,
        keySet.ManifestRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
        keySet.ApproverSetRevision.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
