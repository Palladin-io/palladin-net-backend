using NodaTime;
using Palladin.Core.Events;
using Palladin.Core.Types;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Vault.Contracts.Events;

namespace Palladin.Module.Vault.Domain;

internal sealed record AgentSubmissionTarget(
    Guid? SuggestedVaultId,
    Guid? VaultId,
    Guid? EntryId,
    ulong? ExpectedEntryRevision);

// An Agent's encrypted proposal of a new Entry or a new revision of a granted Entry. The backend is a
// relay: Document is the exact canonical signed transport object (ciphertext and wrapped DEKs) and is
// deleted on approval and purge, while the structural lifecycle remains for audit.
internal sealed class AgentEntrySubmission : EventEntityBase
{
    public Guid OrganizationId { get; private set; }
    public Guid Id { get; private set; }
    public Guid AgentId { get; private set; }
    public AgentSubmissionKind Kind { get; private set; }
    public AgentSubmissionEntryType EntryType { get; private set; }
    public Guid? SuggestedVaultId { get; private set; }
    public Guid? TargetVaultId { get; private set; }
    public Guid? TargetEntryId { get; private set; }
    public ulong? ExpectedEntryRevision { get; private set; }
    public AgentSubmissionState State { get; private set; }
    public bool AgentUseSuspended { get; private set; }
    public ulong SubmissionRevision { get; private set; }
    public uint AgentAccessEpoch { get; private set; }
    public uint ApproverKeyVersion { get; private set; }
    public string? Document { get; private set; }
    public byte[] SubmittedDigest { get; private set; } = [];
    public Instant CreatedAt { get; private set; }
    public Instant UpdatedAt { get; private set; }
    public Instant? TrashedAt { get; private set; }
    public Instant? DecidedAt { get; private set; }
    public Guid? DecidedBy { get; private set; }
    public Guid? DecisionVaultId { get; private set; }
    public Guid? DecisionEntryId { get; private set; }
    public Guid? DecisionGrantId { get; private set; }
    public ulong Version { get; private set; }

    private AgentEntrySubmission() { }

    internal static AgentEntrySubmission Submit(
        Guid organizationId,
        Guid id,
        Guid agentId,
        AgentSubmissionKind kind,
        AgentSubmissionEntryType entryType,
        AgentSubmissionTarget target,
        uint agentAccessEpoch,
        uint approverKeyVersion,
        string document,
        Instant now)
    {
        if (organizationId == Guid.Empty || id == Guid.Empty || agentId == Guid.Empty
            || approverKeyVersion == 0 || string.IsNullOrEmpty(document)
            || (kind == AgentSubmissionKind.Update
                && (target.VaultId is null || target.EntryId is null || target.ExpectedEntryRevision is null))
            || (kind == AgentSubmissionKind.New && (target.VaultId is not null || target.EntryId is not null)))
        {
            throw new DomainException("Agent submission identity is invalid.");
        }

        var submission = new AgentEntrySubmission
        {
            OrganizationId = organizationId,
            Id = id,
            AgentId = agentId,
            Kind = kind,
            EntryType = entryType,
            SuggestedVaultId = target.SuggestedVaultId,
            TargetVaultId = target.VaultId,
            TargetEntryId = target.EntryId,
            ExpectedEntryRevision = target.ExpectedEntryRevision,
            State = AgentSubmissionState.Pending,
            SubmissionRevision = 1,
            AgentAccessEpoch = agentAccessEpoch,
            ApproverKeyVersion = approverKeyVersion,
            Document = document,
            SubmittedDigest = DigestOf(document),
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1,
        };
        submission.Emit(AgentSubmissionAction.Submitted, now);
        return submission;
    }

    internal bool HasDocument(string document) => string.Equals(Document, document, StringComparison.Ordinal);

    internal bool WasSubmittedAs(string document) => DigestOf(document).AsSpan().SequenceEqual(SubmittedDigest);

    private static byte[] DigestOf(string document) =>
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(document));

    internal void Rewrap(ulong submissionRevision, uint agentAccessEpoch, uint approverKeyVersion, string document, Instant now)
    {
        if (State != AgentSubmissionState.Pending || Document is null)
        {
            throw new AgentSubmissionConflictException("submission-not-pending");
        }

        if (submissionRevision != SubmissionRevision + 1)
        {
            throw new AgentSubmissionConflictException("revision-replay");
        }

        SubmissionRevision = submissionRevision;
        AgentAccessEpoch = agentAccessEpoch;
        ApproverKeyVersion = approverKeyVersion;
        Document = document;
        Touch(now);
        Emit(AgentSubmissionAction.Rewrapped, now);
    }

    internal bool IsUsableBy(Guid agentId, uint accessEpoch) =>
        AgentId == agentId
        && State == AgentSubmissionState.Pending
        && !AgentUseSuspended
        && AgentAccessEpoch == accessEpoch
        && Document is not null;

    internal AgentSubmissionChangedEvent UseEvent(Instant now) => ToEvent(AgentSubmissionAction.Used, now);

    internal bool IsApprovedAs(Guid decidedBy, Guid vaultId, Guid entryId) =>
        State == AgentSubmissionState.Approved
        && DecidedBy == decidedBy
        && DecisionVaultId == vaultId
        && DecisionEntryId == entryId;

    internal void Approve(Guid decidedBy, Guid vaultId, Guid entryId, Guid? grantId, Instant now)
    {
        EnsurePending();
        State = AgentSubmissionState.Approved;
        DecidedBy = decidedBy;
        DecidedAt = now;
        DecisionVaultId = vaultId;
        DecisionEntryId = entryId;
        DecisionGrantId = grantId;
        Document = null;
        Touch(now);
        Emit(AgentSubmissionAction.Approved, now);
    }

    internal void Deny(Guid decidedBy, Instant now)
    {
        EnsurePending();
        State = AgentSubmissionState.Trashed;
        AgentUseSuspended = true;
        DecidedBy = decidedBy;
        DecidedAt = now;
        TrashedAt = now;
        Touch(now);
        Emit(AgentSubmissionAction.Denied, now);
    }

    internal void Restore(Guid restoredBy, Instant now)
    {
        if (State != AgentSubmissionState.Trashed)
        {
            throw new AgentSubmissionConflictException("submission-not-trashed");
        }

        State = AgentSubmissionState.Pending;
        DecidedBy = restoredBy;
        DecidedAt = now;
        TrashedAt = null;
        Touch(now);
        Emit(AgentSubmissionAction.Restored, now);
    }

    internal void Purge(Instant now)
    {
        if (State != AgentSubmissionState.Trashed)
        {
            throw new AgentSubmissionConflictException("submission-not-trashed");
        }

        State = AgentSubmissionState.Purged;
        Document = null;
        Touch(now);
        Emit(AgentSubmissionAction.Purged, now);
    }

    private void EnsurePending()
    {
        if (State != AgentSubmissionState.Pending)
        {
            throw new AgentSubmissionConflictException("submission-already-decided");
        }
    }

    private void Touch(Instant now)
    {
        UpdatedAt = now;
        Version++;
    }

    private void Emit(AgentSubmissionAction action, Instant now) => AddEvent(ToEvent(action, now));

    private AgentSubmissionChangedEvent ToEvent(AgentSubmissionAction action, Instant now) => new(
        OrganizationId,
        Id,
        AgentId,
        Kind,
        EntryType,
        TargetVaultId,
        TargetEntryId,
        action,
        State,
        action is AgentSubmissionAction.Approved or AgentSubmissionAction.Denied or AgentSubmissionAction.Restored
            ? DecidedBy
            : null,
        DecisionVaultId,
        DecisionEntryId,
        DecisionGrantId,
        now);
}
