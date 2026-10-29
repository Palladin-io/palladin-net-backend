using System.Security.Cryptography;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record AgentSubmissionStateResponse(Guid SubmissionId, string State, string SubmissionRevision);

internal sealed record AuthenticatedAgent(Guid AgentId, Guid OrganizationId, uint AccessEpoch);

// Verifies a submitted or re-wrapped transport object against independent authorities: the authenticated
// Agent, its current replica keys (signature and recipient key), the current approver key set and, for an
// update, the Agent's active grant over the exact target Entry.
internal static class AgentSubmissionIntake
{
    internal static string StateName(AgentSubmissionState state) => state switch
    {
        AgentSubmissionState.Pending => "pending",
        AgentSubmissionState.Approved => "approved",
        AgentSubmissionState.Trashed => "trashed",
        _ => "purged",
    };

    internal static AgentSubmissionStateResponse ToStateResponse(AgentEntrySubmission submission) => new(
        submission.Id,
        StateName(submission.State),
        submission.SubmissionRevision.ToString(System.Globalization.CultureInfo.InvariantCulture));

    internal static void EnsureContext(ParsedAgentSubmission submission, AuthenticatedAgent agent)
    {
        if (submission.OrganizationId != agent.OrganizationId || submission.AgentId != agent.AgentId)
        {
            throw new SubmissionRejection(StatusCodes.Status403Forbidden, "context-mismatch");
        }
    }

    internal static async Task VerifyAgentAsync(
        VaultDomainWriteContext domainWriteContext,
        ParsedAgentSubmission submission,
        AuthenticatedAgent authenticated,
        CancellationToken ct)
    {
        var agent = await domainWriteContext.Agents.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == authenticated.AgentId
                                       && x.OrganizationId == authenticated.OrganizationId
                                       && x.Status == AgentStatus.Active
                                       && x.AccessEpoch == authenticated.AccessEpoch, ct)
            ?? throw new SubmissionRejection(StatusCodes.Status403Forbidden, "agent-not-active");

        if (submission.AgentAccessEpoch != agent.AccessEpoch
            || submission.AgentKeyVersion != agent.RecipientKeyVersion
            || !CryptographicOperations.FixedTimeEquals(
                submission.AgentKeyFingerprint,
                VaultKeyFingerprint.Compute(Convert.FromBase64String(agent.PublicKey), VaultKeyKind.AgentX25519)))
        {
            throw new SubmissionRejection(StatusCodes.Status409Conflict, "agent-key-mismatch");
        }

        if (!AgentSubmissionCrypto.VerifyAgentSignature(submission, Convert.FromBase64String(agent.SigningPublicKey)))
        {
            throw new SubmissionRejection(StatusCodes.Status400BadRequest, "invalid-agent-signature");
        }
    }

    internal static async Task VerifyApproverKeyAsync(
        VaultDomainWriteContext domainWriteContext,
        ParsedAgentSubmission submission,
        CancellationToken ct)
    {
        var state = await domainWriteContext.LoadApproverStateAsync(submission.OrganizationId, ct);
        var keySet = state.KeySet;
        if (keySet is null || !state.HasServedApprover)
        {
            throw new SubmissionRejection(StatusCodes.Status404NotFound, "no-approvers");
        }

        // A removed approver may still hold the current private key; new DEKs wait for the rotation.
        if (state.HasRemovedApproverWrap)
        {
            throw new SubmissionRejection(StatusCodes.Status409Conflict, "approver-key-unavailable");
        }

        if (submission.ApproverKeyVersion != keySet.KeyVersion
            || submission.ApproverManifestRevision != keySet.ManifestRevision
            || !CryptographicOperations.FixedTimeEquals(
                submission.ApproverMessageKeyFingerprint, keySet.MessageKeyFingerprint))
        {
            throw new SubmissionRejection(StatusCodes.Status409Conflict, "approver-key-rotated");
        }
    }

    internal static async Task VerifyUpdateGrantAsync(
        VaultDomainWriteContext domainWriteContext,
        ParsedAgentSubmission submission,
        AuthenticatedAgent agent,
        CancellationToken ct)
    {
        if (submission.Kind != AgentSubmissionKind.Update)
        {
            return;
        }

        if (!await domainWriteContext.HasActiveEntryCoverageAsync(
                agent.AgentId, agent.AccessEpoch, submission.Target.VaultId!.Value, submission.Target.EntryId!.Value,
                excludingGrantId: null, ct))
        {
            throw new SubmissionRejection(StatusCodes.Status403Forbidden, "update-not-granted");
        }
    }
}
