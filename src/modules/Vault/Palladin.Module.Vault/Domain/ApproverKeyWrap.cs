using Palladin.Core.Types.Exceptions;

namespace Palladin.Module.Vault.Domain;

// One approver private key (X25519 message key or Ed25519 signing seed) sealed client-side to one
// approver's current Member X25519 key. The backend stores and serves ciphertext only.
internal sealed class ApproverKeyWrap
{
    internal const ushort MessagePrivateKeyPurpose = 8;
    internal const ushort SigningPrivateKeyPurpose = 9;
    internal const int SealedPackageBytes = 120;

    public Guid OrganizationId { get; private set; }
    public Guid MemberId { get; private set; }
    public ushort Purpose { get; private set; }
    public uint KeyVersion { get; private set; }
    public ulong ApproverSetRevision { get; private set; }
    public MemberRecipientKeyVersion RecipientKeyVersion { get; private set; }
    public byte[] RecipientFingerprint { get; private set; } = [];
    public byte[] SealedPackage { get; private set; } = [];

    private ApproverKeyWrap() { }

    internal static ApproverKeyWrap Create(
        Guid organizationId,
        Guid memberId,
        ushort purpose,
        uint keyVersion,
        ulong approverSetRevision,
        MemberRecipientKeyVersion recipientKeyVersion,
        byte[] recipientFingerprint,
        byte[] sealedPackage)
    {
        if (organizationId == Guid.Empty || memberId == Guid.Empty
            || purpose is not (MessagePrivateKeyPurpose or SigningPrivateKeyPurpose)
            || keyVersion == 0 || approverSetRevision == 0
            || recipientFingerprint.Length != VaultProtocol.FingerprintBytes
            || sealedPackage.Length != SealedPackageBytes)
        {
            throw new DomainException("Approver key wrap structure is invalid.");
        }

        return new ApproverKeyWrap
        {
            OrganizationId = organizationId,
            MemberId = memberId,
            Purpose = purpose,
            KeyVersion = keyVersion,
            ApproverSetRevision = approverSetRevision,
            RecipientKeyVersion = recipientKeyVersion,
            RecipientFingerprint = recipientFingerprint.ToArray(),
            SealedPackage = sealedPackage.ToArray(),
        };
    }

    internal bool IsAddressedTo(MemberKeyDirectoryEntry directoryEntry) =>
        directoryEntry.UserId == MemberId
        && directoryEntry.KeyVersion == RecipientKeyVersion
        && directoryEntry.Fingerprint.AsSpan().SequenceEqual(RecipientFingerprint);
}
