using Palladin.Core.Types.Exceptions;

namespace Palladin.Module.Vault.Domain;

// One approver private key (message key or Ed25519 signing seed) sealed client-side to one approver's
// current Member key under that key's suite. The backend stores and serves ciphertext only.
internal sealed class ApproverKeyWrap
{
    internal const ushort MessagePrivateKeyPurpose = 8;
    internal const ushort SigningPrivateKeyPurpose = 9;

    public Guid OrganizationId { get; private set; }
    public Guid MemberId { get; private set; }
    public ushort Purpose { get; private set; }
    public uint KeyVersion { get; private set; }
    public ulong ApproverSetRevision { get; private set; }
    public MemberRecipientKeyVersion RecipientKeyVersion { get; private set; }
    public byte[] RecipientFingerprint { get; private set; } = [];
    public byte[] SealedPackage { get; private set; } = [];
    public string WrapperSuiteId { get; private set; } = string.Empty;

    private ApproverKeyWrap() { }

    internal static ApproverKeyWrap Create(
        Guid organizationId,
        Guid memberId,
        ushort purpose,
        uint keyVersion,
        ulong approverSetRevision,
        MemberRecipientKeyVersion recipientKeyVersion,
        byte[] recipientFingerprint,
        string wrapperSuiteId,
        byte[] sealedPackage)
    {
        if (organizationId == Guid.Empty || memberId == Guid.Empty
            || purpose is not (MessagePrivateKeyPurpose or SigningPrivateKeyPurpose)
            || keyVersion == 0 || approverSetRevision == 0
            || recipientFingerprint.Length != VaultProtocol.FingerprintBytes
            || sealedPackage.Length != Palladin.Core.Cryptography.KeyWrapperSuites.ResolveForOpen(wrapperSuiteId).PackageBytes)
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
            WrapperSuiteId = wrapperSuiteId,
        };
    }

    internal bool IsAddressedTo(MemberKeyDirectoryEntry directoryEntry) =>
        directoryEntry.UserId == MemberId
        && directoryEntry.KeyVersion == RecipientKeyVersion
        && directoryEntry.Fingerprint.AsSpan().SequenceEqual(RecipientFingerprint);
}
