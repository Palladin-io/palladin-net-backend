using NodaTime;
using Palladin.Core.Types.Exceptions;

namespace Palladin.Module.Vault.Domain;

// Server-owned, single-use manifest challenge. One active row per approver bounds storage; issuing
// replaces the previous challenge of the same approver.
internal sealed class ApproverKeyChallenge
{
    public Guid OrganizationId { get; private set; }
    public Guid IssuedTo { get; private set; }
    public Guid Id { get; private set; }
    public Instant IssuedAt { get; private set; }
    public Instant ExpiresAt { get; private set; }
    public ulong ApproverSetRevision { get; private set; }
    public byte[] ApproverSetDigest { get; private set; } = [];

    private ApproverKeyChallenge() { }

    internal static ApproverKeyChallenge Issue(
        Guid organizationId,
        Guid issuedTo,
        Guid id,
        Instant issuedAt,
        Duration lifetime,
        ulong approverSetRevision,
        byte[] approverSetDigest)
    {
        var challenge = new ApproverKeyChallenge { OrganizationId = organizationId, IssuedTo = issuedTo };
        challenge.Reissue(id, issuedAt, lifetime, approverSetRevision, approverSetDigest);
        return challenge;
    }

    internal void Reissue(
        Guid id,
        Instant issuedAt,
        Duration lifetime,
        ulong approverSetRevision,
        byte[] approverSetDigest)
    {
        if (id == Guid.Empty || lifetime <= Duration.Zero || approverSetRevision == 0
            || approverSetDigest.Length != 32)
        {
            throw new DomainException("Approver key challenge is invalid.");
        }

        Id = id;
        IssuedAt = issuedAt;
        ExpiresAt = issuedAt + lifetime;
        ApproverSetRevision = approverSetRevision;
        ApproverSetDigest = approverSetDigest.ToArray();
    }

    internal bool IsUsable(Guid id, Instant now) => Id == id && now < ExpiresAt;
}
