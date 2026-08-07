using Palladin.Core.Types.Exceptions;

namespace Palladin.Module.Vault.Domain;

internal sealed record MemberWrappedVaultKey
{
    internal VaultScope Scope { get; }
    internal Guid MemberId { get; }
    internal ushort ProtocolVersion { get; }
    internal string WrapperSuiteId { get; }
    internal VaultKeyVersion VaultKeyVersion { get; }
    internal MemberKeyGeneration MemberKeyGeneration { get; }
    internal MemberRecipientKeyVersion RecipientKeyVersion { get; }
    internal byte[] RecipientKeyFingerprint { get; }
    internal byte[] SealedVaultKeyPackage { get; }

    private MemberWrappedVaultKey(
        VaultScope scope,
        Guid memberId,
        ushort protocolVersion,
        string wrapperSuiteId,
        VaultKeyVersion vaultKeyVersion,
        MemberKeyGeneration memberKeyGeneration,
        MemberRecipientKeyVersion recipientKeyVersion,
        byte[] recipientKeyFingerprint,
        byte[] sealedVaultKeyPackage)
    {
        Scope = scope;
        MemberId = memberId;
        ProtocolVersion = protocolVersion;
        WrapperSuiteId = wrapperSuiteId;
        VaultKeyVersion = vaultKeyVersion;
        MemberKeyGeneration = memberKeyGeneration;
        RecipientKeyVersion = recipientKeyVersion;
        RecipientKeyFingerprint = recipientKeyFingerprint.ToArray();
        SealedVaultKeyPackage = sealedVaultKeyPackage.ToArray();
    }

    internal static MemberWrappedVaultKey Create(
        VaultScope scope,
        Guid memberId,
        ushort protocolVersion,
        string wrapperSuiteId,
        VaultKeyVersion vaultKeyVersion,
        MemberKeyGeneration memberKeyGeneration,
        MemberRecipientKeyVersion recipientKeyVersion,
        byte[] recipientKeyFingerprint,
        byte[] sealedVaultKeyPackage)
    {
        scope.Validate();

        if (memberId == Guid.Empty)
        {
            throw new DomainException("Vault Member identifier must not be empty.");
        }

        if (protocolVersion != VaultProtocol.CurrentVersion
            || !string.Equals(wrapperSuiteId, Infrastructure.Crypto.X25519SealedBoxContract.SuiteId, StringComparison.Ordinal))
        {
            throw new DomainException("Wrapped Vault key is incompatible with Vault protocol 2.");
        }

        if (recipientKeyFingerprint.Length != VaultProtocol.FingerprintBytes)
        {
            throw new DomainException("Member recipient key fingerprint must contain exactly 32 bytes.");
        }

        if (sealedVaultKeyPackage.Length != Infrastructure.Crypto.X25519SealedBoxContract.EncodedPackageBytes)
        {
            throw new DomainException("Sealed Vault key package size is outside the protocol limit.");
        }

        return new MemberWrappedVaultKey(
            scope,
            memberId,
            protocolVersion,
            wrapperSuiteId,
            vaultKeyVersion,
            memberKeyGeneration,
            recipientKeyVersion,
            recipientKeyFingerprint,
            sealedVaultKeyPackage);
    }

    internal static MemberWrappedVaultKey Create(
        VaultScope scope, Guid memberId, ushort protocolVersion, ushort _, VaultKeyVersion vaultKeyVersion,
        MemberKeyGeneration memberKeyGeneration, MemberRecipientKeyVersion recipientKeyVersion,
        byte[] recipientKeyFingerprint, byte[] sealedVaultKeyPackage) =>
        Create(scope, memberId, protocolVersion, Infrastructure.Crypto.X25519SealedBoxContract.SuiteId,
            vaultKeyVersion, memberKeyGeneration, recipientKeyVersion, recipientKeyFingerprint,
            sealedVaultKeyPackage);
}

internal sealed class VaultMemberKeyEnvelope
{
    internal Guid OrganizationId { get; private set; }
    internal Guid VaultId { get; private set; }
    internal Guid MemberId { get; private set; }
    internal MemberKeyGeneration MemberKeyGeneration { get; private set; }
    internal ushort ProtocolVersion { get; private set; }
    internal string WrapperSuiteId { get; private set; } = string.Empty;
    internal VaultKeyVersion VaultKeyVersion { get; private set; }
    internal MemberRecipientKeyVersion RecipientKeyVersion { get; private set; }
    internal byte[] RecipientKeyFingerprint { get; private set; } = [];
    internal byte[] SealedVaultKeyPackage { get; private set; } = [];

    internal Vault Vault { get; private set; } = null!;

    private VaultMemberKeyEnvelope() { }

    internal static VaultMemberKeyEnvelope Create(MemberWrappedVaultKey wrappedVaultKey) => new()
    {
        OrganizationId = wrappedVaultKey.Scope.OrganizationId,
        VaultId = wrappedVaultKey.Scope.VaultId,
        MemberId = wrappedVaultKey.MemberId,
        MemberKeyGeneration = wrappedVaultKey.MemberKeyGeneration,
        ProtocolVersion = wrappedVaultKey.ProtocolVersion,
        WrapperSuiteId = wrappedVaultKey.WrapperSuiteId,
        VaultKeyVersion = wrappedVaultKey.VaultKeyVersion,
        RecipientKeyVersion = wrappedVaultKey.RecipientKeyVersion,
        RecipientKeyFingerprint = wrappedVaultKey.RecipientKeyFingerprint.ToArray(),
        SealedVaultKeyPackage = wrappedVaultKey.SealedVaultKeyPackage.ToArray(),
    };

    internal MemberWrappedVaultKey GetWrappedVaultKey() => MemberWrappedVaultKey.Create(
        new VaultScope(OrganizationId, VaultId),
        MemberId,
        ProtocolVersion,
        WrapperSuiteId,
        VaultKeyVersion,
        MemberKeyGeneration,
        RecipientKeyVersion,
        RecipientKeyFingerprint,
        SealedVaultKeyPackage);
}
