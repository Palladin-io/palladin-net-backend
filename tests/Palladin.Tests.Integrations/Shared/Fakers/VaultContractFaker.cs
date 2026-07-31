using Microsoft.AspNetCore.WebUtilities;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Shared;

namespace Palladin.Tests.Integrations.Shared.Fakers;

internal static class VaultContractFaker
{
    internal static byte[] MemberPublicKey => Enumerable.Range(0, 32).Select(x => (byte)x).ToArray();

    internal static CreateVaultRequest CreateRequest(
        Guid organizationId,
        Guid vaultId,
        Guid memberId,
        ulong revision = 1)
    {
        var fingerprint = WebEncoders.Base64UrlEncode(MemberKeyFingerprint.Compute(MemberPublicKey));
        var agentMessageKey = VaultTrustAnchorFaker.AgentMessagePublicKey;
        var signingKey = VaultTrustAnchorFaker.ManifestSigningPublicKey;

        return new CreateVaultRequest
        {
            VaultId = vaultId,
            MemberVaultMetadata = new MemberVaultMetadataEnvelopeContract(
                new EnvelopeDescriptorContract<EmptyEnvelopeBindingContract>(2, CryptoSuiteId.XChaCha20Poly1305V1,
                    EnvelopePurposeContract.MemberVaultMetadata,
                    new EnvelopeScopeContract(organizationId, vaultId), revision.ToString(), 1, 1,
                    new EmptyEnvelopeBindingContract()),
                WebEncoders.Base64UrlEncode(Enumerable.Range(0, 24 + 48).Select(x => (byte)(x + (int)revision)).ToArray())),
            CurrentKeyEpoch = new VaultKeyEpochContract(1, 1, 1, 1),
            CreatorVaultKey = new MemberVaultKeyEnvelopeContract(
                new X25519WrappedKeyContract(
                    new X25519WrapperDescriptorContract(2, X25519SealedBoxContract.SuiteId,
                        X25519WrapperPurposeContract.MemberVaultKey,
                        new EnvelopeScopeContract(organizationId, vaultId, MemberId: memberId), "1", 1, 1,
                        X25519RecipientKeyKindContract.MemberX25519, 1, fingerprint, null),
                    WebEncoders.Base64UrlEncode(Enumerable.Range(0, 120).Select(x => (byte)(x + 1)).ToArray()))),
            DiscoveryKey = CreateDiscoveryKey(organizationId, vaultId),
            VaultPrivateKeys = CreatePrivateKeys(organizationId, vaultId),
            VaultAgentMessagePublicKey = PublicKey(VaultPublicKeyKindContract.AgentMessageX25519,
                "palladin-x25519-v1", 1, agentMessageKey, VaultKeyKind.VaultMessageX25519),
            VaultManifestSigningPublicKey = PublicKey(VaultPublicKeyKindContract.ManifestSigningEd25519,
                "palladin-ed25519-v1", 1, signingKey, VaultKeyKind.VaultSigningEd25519),
        };
    }

    private static VaultPublicKeyContract PublicKey(VaultPublicKeyKindContract kind, string scheme, uint version,
        byte[] key, VaultKeyKind fingerprintKind) => new(2, scheme, kind, version,
        WebEncoders.Base64UrlEncode(key), WebEncoders.Base64UrlEncode(VaultKeyFingerprint.Compute(key, fingerprintKind)));

    internal static VaultDiscoveryKeyEnvelopeContract CreateDiscoveryKey(
        Guid organizationId,
        Guid vaultId,
        ulong revision = 1,
        uint memberKeyGeneration = 1,
        uint vaultKeyVersion = 1,
        uint vdkVersion = 1,
        uint agentMessageKeyVersion = 1,
        uint manifestSigningKeyVersion = 1)
    {
        var envelope = CreateKeyMaterialEnvelope(organizationId, vaultId, VaultKeyMaterialKind.DiscoveryKey,
            revision, vdkVersion, memberKeyGeneration, vaultKeyVersion, 1);
        return VaultEnvelopeContractMapper.ToDiscoveryKeyContract(envelope);
    }

    internal static VaultPrivateKeyEnvelopeContract[] CreatePrivateKeys(
        Guid organizationId,
        Guid vaultId,
        ulong revision = 1,
        uint memberKeyGeneration = 1,
        uint vaultKeyVersion = 1,
        uint vdkVersion = 1,
        uint agentMessageKeyVersion = 1,
        uint manifestSigningKeyVersion = 1) =>
        [
            VaultEnvelopeContractMapper.ToPrivateKeyContract(CreateKeyMaterialEnvelope(
                organizationId, vaultId, VaultKeyMaterialKind.AgentMessagePrivateKey,
                revision, agentMessageKeyVersion, memberKeyGeneration, vaultKeyVersion, 2)),
            VaultEnvelopeContractMapper.ToPrivateKeyContract(CreateKeyMaterialEnvelope(
                organizationId, vaultId, VaultKeyMaterialKind.ManifestSigningPrivateKey,
                revision, manifestSigningKeyVersion, memberKeyGeneration, vaultKeyVersion, 3)),
        ];

    internal static VaultPublicKeyContract CreateAgentMessagePublicKey(uint version = 2) =>
        PublicKey(VaultPublicKeyKindContract.AgentMessageX25519, "palladin-x25519-v1", version,
            VaultTrustAnchorFaker.RotatedAgentMessagePublicKey, VaultKeyKind.VaultMessageX25519);

    internal static VaultPublicKeyContract CreateManifestSigningPublicKey(uint version = 2) =>
        PublicKey(VaultPublicKeyKindContract.ManifestSigningEd25519, "palladin-ed25519-v1", version,
            VaultTrustAnchorFaker.RotatedManifestSigningPublicKey, VaultKeyKind.VaultSigningEd25519);

    private static VaultKeyMaterialEnvelope CreateKeyMaterialEnvelope(
        Guid organizationId,
        Guid vaultId,
        VaultKeyMaterialKind kind,
        ulong revision,
        uint keyVersion,
        uint memberKeyGeneration,
        uint vaultKeyVersion,
        int offset)
    {
        var nonce = WebEncoders.Base64UrlEncode(
            Enumerable.Range(0, VaultProtocol.NonceBytes).Select(x => (byte)(x + offset)).ToArray());
        return VaultKeyMaterialEnvelope.Create(
            new VaultScope(organizationId, vaultId),
            kind,
            revision,
            keyVersion,
            new MemberKeyGeneration(memberKeyGeneration),
            new VaultKeyVersion(vaultKeyVersion),
            new VaultKeyMaterialHeader(
                VaultProtocol.CurrentVersion,
                VaultProtocol.AlgorithmSuite,
                VaultProtocol.VaultResourceKind,
                kind == VaultKeyMaterialKind.DiscoveryKey
                    ? VaultProtocol.VaultDiscoveryKeyProjectionKind
                    : VaultProtocol.VaultPrivateKeyProjectionKind,
                revision,
                keyVersion,
                new MemberKeyGeneration(memberKeyGeneration),
                WebEncoders.Base64UrlDecode(nonce)),
            Enumerable.Range(0, 48).Select(x => (byte)(x + offset)).ToArray());
    }
}
