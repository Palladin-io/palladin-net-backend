using NodaTime;
using Palladin.Core.Types.Exceptions;

namespace Palladin.Module.Vault.Domain;

internal sealed record ApproverManifest(
    Guid OrganizationId,
    uint KeyVersion,
    ulong ManifestRevision,
    ulong ApproverSetRevision,
    byte[] ApproverSetDigest,
    byte[] MessagePublicKey,
    byte[] MessageKeyFingerprint,
    byte[] SigningPublicKey,
    byte[] SigningKeyFingerprint,
    byte[]? PreviousSigningKeyFingerprint,
    string Document);

// The organization approver group key: one versioned X25519 message key and Ed25519 signing key,
// shared by current approvers through per-approver wraps. Only public keys and ciphertext are stored.
internal sealed class OrganizationApproverKeySet
{
    public Guid OrganizationId { get; private set; }
    public uint KeyVersion { get; private set; }
    public ulong ManifestRevision { get; private set; }
    public ulong ApproverSetRevision { get; private set; }
    public byte[] MessagePublicKey { get; private set; } = [];
    public byte[] MessageKeyFingerprint { get; private set; } = [];
    public byte[] SigningPublicKey { get; private set; } = [];
    public byte[] SigningKeyFingerprint { get; private set; } = [];
    public byte[]? PreviousSigningKeyFingerprint { get; private set; }
    public Instant CreatedAt { get; private set; }
    public Instant UpdatedAt { get; private set; }
    public ulong Version { get; private set; }
    public ICollection<ApproverKeyWrap> Wraps { get; private set; } = [];
    public ICollection<OrganizationApproverManifestRecord> Manifests { get; private set; } = [];

    private OrganizationApproverKeySet() { }

    internal static OrganizationApproverKeySet Bootstrap(
        ApproverManifest manifest,
        IReadOnlyCollection<ApproverKeyWrap> wraps,
        Instant now)
    {
        if (manifest.KeyVersion != 1 || manifest.ManifestRevision != 1
            || manifest.PreviousSigningKeyFingerprint is not null)
        {
            throw new DomainException("A bootstrap approver manifest must start key version and revision at 1.");
        }

        var keySet = new OrganizationApproverKeySet
        {
            OrganizationId = manifest.OrganizationId,
            CreatedAt = now,
        };
        keySet.Apply(manifest, now);
        keySet.AddWraps(wraps);
        return keySet;
    }

    internal void AddApprovers(ApproverManifest manifest, IReadOnlyCollection<ApproverKeyWrap> wraps, Instant now)
    {
        if (manifest.KeyVersion != KeyVersion
            || manifest.ManifestRevision != ManifestRevision + 1
            || manifest.ApproverSetRevision != ApproverSetRevision + 1
            || !manifest.SigningPublicKey.AsSpan().SequenceEqual(SigningPublicKey)
            || !manifest.MessagePublicKey.AsSpan().SequenceEqual(MessagePublicKey)
            || manifest.PreviousSigningKeyFingerprint is not null)
        {
            throw new AgentSubmissionConflictException("approver-manifest-stale");
        }

        Apply(manifest, now);
        AddWraps(wraps);
    }

    internal void Rotate(ApproverManifest manifest, IReadOnlyCollection<ApproverKeyWrap> wraps, Instant now)
    {
        if (KeyVersion == uint.MaxValue
            || manifest.KeyVersion != KeyVersion + 1
            || manifest.ManifestRevision != ManifestRevision + 1
            || manifest.ApproverSetRevision != ApproverSetRevision + 1
            || manifest.PreviousSigningKeyFingerprint is null
            || !manifest.PreviousSigningKeyFingerprint.AsSpan().SequenceEqual(SigningKeyFingerprint))
        {
            throw new AgentSubmissionConflictException("approver-manifest-stale");
        }

        foreach (var wrap in Wraps.ToArray())
        {
            Wraps.Remove(wrap);
        }

        Apply(manifest, now);
        AddWraps(wraps);
    }

    // Contract approverKeySet.reBootstrap: once no current approver can open the key set, a current approver
    // introduces the next key version without continuity; Agents must re-trust it explicitly.
    internal void Rebootstrap(ApproverManifest manifest, IReadOnlyCollection<ApproverKeyWrap> wraps, Instant now)
    {
        if (KeyVersion == uint.MaxValue
            || manifest.KeyVersion != KeyVersion + 1
            || manifest.ManifestRevision != ManifestRevision + 1
            || manifest.ApproverSetRevision != ApproverSetRevision + 1
            || manifest.PreviousSigningKeyFingerprint is not null)
        {
            throw new AgentSubmissionConflictException("approver-manifest-stale");
        }

        foreach (var wrap in Wraps.ToArray())
        {
            Wraps.Remove(wrap);
        }

        Apply(manifest, now);
        AddWraps(wraps);
    }

    internal IEnumerable<ApproverKeyWrap> CurrentWrapsOf(Guid memberId) =>
        Wraps.Where(wrap => wrap.MemberId == memberId && wrap.KeyVersion == KeyVersion);

    private void Apply(ApproverManifest manifest, Instant now)
    {
        if (manifest.OrganizationId != OrganizationId)
        {
            throw new DomainException("Approver manifest organization does not match the key set.");
        }

        KeyVersion = manifest.KeyVersion;
        ManifestRevision = manifest.ManifestRevision;
        ApproverSetRevision = manifest.ApproverSetRevision;
        MessagePublicKey = manifest.MessagePublicKey.ToArray();
        MessageKeyFingerprint = manifest.MessageKeyFingerprint.ToArray();
        SigningPublicKey = manifest.SigningPublicKey.ToArray();
        SigningKeyFingerprint = manifest.SigningKeyFingerprint.ToArray();
        PreviousSigningKeyFingerprint = manifest.PreviousSigningKeyFingerprint?.ToArray();
        UpdatedAt = now;
        Version++;
        Manifests.Add(OrganizationApproverManifestRecord.Create(
            OrganizationId, manifest.ManifestRevision, manifest.KeyVersion, manifest.Document));
    }

    private void AddWraps(IReadOnlyCollection<ApproverKeyWrap> wraps)
    {
        if (wraps.Count == 0 || wraps.Any(wrap => wrap.OrganizationId != OrganizationId
                || wrap.KeyVersion != KeyVersion
                || wrap.ApproverSetRevision != ApproverSetRevision))
        {
            throw new DomainException("Approver key wraps must bind the committed key version and approver set revision.");
        }

        foreach (var wrap in wraps)
        {
            Wraps.Add(wrap);
        }
    }

}
