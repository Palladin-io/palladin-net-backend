using JetBrains.Annotations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record SubmissionRouteRequest
{
    public Guid SubmissionId { get; init; }
}

[PublicAPI]
public sealed record ApproveSubmissionResponse(
    Guid SubmissionId,
    Guid VaultId,
    Guid EntryId,
    string EntryRevision,
    Guid? GrantId);

internal sealed record SubmissionDecisionContext(AgentEntrySubmission Submission, ApproverKeySetState State);

// Every decision requires a current approver: GrantManage plus a served wrap of the current approver key,
// so a removed or re-keyed approver cannot decide before the rotation commits.
internal static class SubmissionDecision
{
    internal static async Task<SubmissionDecisionContext> LoadAsync(
        VaultDomainWriteContext domainWriteContext,
        Guid organizationId,
        Guid userId,
        Guid submissionId,
        CancellationToken ct)
    {
        var submission = await domainWriteContext.AgentEntrySubmissions
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == submissionId, ct)
            ?? throw new SubmissionRejection(StatusCodes.Status404NotFound, "submission-not-found");
        var state = await domainWriteContext.LoadApproverStateAsync(organizationId, ct);
        if (!state.IsServed(userId))
        {
            throw new SubmissionRejection(StatusCodes.Status403Forbidden, "approver-not-current");
        }

        return new SubmissionDecisionContext(submission, state);
    }

    internal static void EnsureApprovable(SubmissionDecisionContext context)
    {
        if (context.Submission.State != AgentSubmissionState.Pending)
        {
            throw new SubmissionRejection(StatusCodes.Status409Conflict, "submission-already-decided");
        }

        if (context.Submission.ApproverKeyVersion != context.State.KeySet!.KeyVersion)
        {
            throw new SubmissionRejection(StatusCodes.Status409Conflict, "approver-key-rotated");
        }
    }

    internal static async Task EnsureVaultManagerAsync(
        VaultDomainWriteContext domainWriteContext,
        Permission permissions,
        Guid organizationId,
        Guid vaultId,
        Guid userId,
        CancellationToken ct)
    {
        if (!permissions.HasFlag(Permission.VaultManage)
            || !await domainWriteContext.VaultMembers.AnyAsync(
                x => x.OrganizationId == organizationId && x.VaultId == vaultId && x.UserId == userId, ct))
        {
            throw new SubmissionRejection(StatusCodes.Status403Forbidden, "vault-access-required");
        }
    }

    internal static void EnsureGrantTargets(
        CreateGranularGrantRequest? grant,
        AgentEntrySubmission submission,
        Guid vaultId,
        Guid entryId)
    {
        if (grant is not null
            && (grant.AgentId != submission.AgentId || grant.VaultId != vaultId || grant.EntryId != entryId))
        {
            throw new SubmissionRejection(StatusCodes.Status400BadRequest, "grant-target-mismatch");
        }
    }

    internal static async Task<Guid?> StageGrantAsync(
        VaultDomainWriteContext domainWriteContext,
        CreateGranularGrantRequest? grant,
        Guid organizationId,
        Guid userId,
        NodaTime.Instant now,
        VaultEntry entry,
        CancellationToken ct)
    {
        if (grant is null)
        {
            return null;
        }

        try
        {
            var staging = await GranularGrantCreation.StageAsync(
                domainWriteContext, grant, organizationId, userId, now, ct, entry);
            if (staging.Failure is { } failure)
            {
                throw new SubmissionRejection(failure.StatusCode, "grant-rejected");
            }

            return staging.GrantId;
        }
        catch (AgentAlreadyHasActiveAccessException)
        {
            throw new SubmissionRejection(StatusCodes.Status409Conflict, "agent-already-has-access");
        }
    }

    // A racing decision can surface as the submission's concurrency token or as whichever unique constraint
    // (Entry, sequence, PK_Grants) the winner's rows hit first; the re-read state decides which loser it was.
    internal static async Task<SubmissionRejection> ConcurrencyRejectionAsync(
        VaultDomainWriteContext domainWriteContext,
        Guid organizationId,
        Guid submissionId,
        CancellationToken ct,
        string pendingCode = "concurrent-modification")
    {
        domainWriteContext.Clear();
        var state = await domainWriteContext.AgentEntrySubmissions
            .Where(x => x.OrganizationId == organizationId && x.Id == submissionId)
            .Select(x => (AgentSubmissionState?)x.State)
            .SingleOrDefaultAsync(ct);
        return new SubmissionRejection(
            StatusCodes.Status409Conflict,
            state == AgentSubmissionState.Pending ? pendingCode : "submission-already-decided");
    }

    // A loser that read the pending state before the winner committed can meet the winner's rows during staging
    // (existing Entry, grant coverage); any such conflict is reported as the lost decision it really is.
    internal static async Task<SubmissionRejection> ResolveRaceAsync(
        SubmissionRejection rejection,
        VaultDomainWriteContext domainWriteContext,
        Guid organizationId,
        Guid submissionId,
        CancellationToken ct) =>
        rejection.StatusCode == StatusCodes.Status409Conflict && rejection.Code != "submission-already-decided"
            ? await ConcurrencyRejectionAsync(domainWriteContext, organizationId, submissionId, ct, pendingCode: rejection.Code)
            : rejection;

    internal static ApproveSubmissionResponse ToResponse(AgentEntrySubmission submission, VaultEntry entry) => new(
        submission.Id,
        entry.VaultId,
        entry.Id,
        entry.CurrentRevision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        submission.DecisionGrantId);
}
