using NodaTime;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Crypto;

namespace Palladin.Tests.Integrations.Shared.Fakers;

internal static class VaultFaker
{
    internal static Vault Create(
        Guid? id = null,
        Guid? organizationId = null,
        Guid? createdBy = null,
        bool isDefault = false)
    {
        var vaultId = id ?? Guid.NewGuid();
        var tenantId = organizationId ?? Guid.NewGuid();
        var userId = createdBy ?? Guid.NewGuid();
        var scope = new VaultScope(tenantId, vaultId);
        var generation = new MemberKeyGeneration(1);
        var epoch = new VaultKeyEpoch(
            new VaultKeyVersion(1),
            new VdkVersion(1),
            new AgentMessageKeyVersion(1),
            new ManifestSigningKeyVersion(1));
        var metadata = CreateMetadata(scope, 1, generation, epoch.VaultKeyVersion);
        var memberKey = CreateMemberKey(scope, userId, generation, epoch.VaultKeyVersion);
        var keyMaterial = CreateKeyMaterial(scope, generation, epoch);
        var now = SystemClock.Instance.GetCurrentInstant();

        return isDefault
            ? Vault.CreateDefault(vaultId, tenantId, userId, "test actor", metadata, generation, epoch, memberKey,
                keyMaterial, CreatePublicKey(VaultPublicKeyKind.AgentMessageX25519, epoch.AgentMessageKeyVersion.Value),
                CreatePublicKey(VaultPublicKeyKind.ManifestSigningEd25519, epoch.ManifestSigningKeyVersion.Value), now)
            : Vault.Create(vaultId, tenantId, userId, "test actor", metadata, generation, epoch, memberKey,
                keyMaterial, CreatePublicKey(VaultPublicKeyKind.AgentMessageX25519, epoch.AgentMessageKeyVersion.Value),
                CreatePublicKey(VaultPublicKeyKind.ManifestSigningEd25519, epoch.ManifestSigningKeyVersion.Value), now);
    }

    private static ValidatedVaultPublicKey CreatePublicKey(VaultPublicKeyKind kind, uint version)
    {
        var key = kind == VaultPublicKeyKind.AgentMessageX25519
            ? VaultTrustAnchorFaker.AgentMessagePublicKey
            : VaultTrustAnchorFaker.ManifestSigningPublicKey;
        var fingerprintKind = kind == VaultPublicKeyKind.AgentMessageX25519
            ? VaultKeyKind.VaultMessageX25519
            : VaultKeyKind.VaultSigningEd25519;
        return new ValidatedVaultPublicKey(kind, version, key, VaultKeyFingerprint.Compute(key, fingerprintKind));
    }

    internal static MemberVaultMetadataCiphertext CreateMetadata(
        VaultScope scope,
        ulong revision,
        MemberKeyGeneration? generation = null,
        VaultKeyVersion? keyVersion = null)
    {
        var metadataRevision = new MetadataRevision(revision);
        var header = MemberVaultMetadataHeader.Create(
            VaultProtocol.CurrentVersion,
            VaultProtocol.AlgorithmSuite,
            VaultProtocol.VaultResourceKind,
            VaultProtocol.MemberVaultMetadataProjectionKind,
            metadataRevision,
            keyVersion ?? new VaultKeyVersion(1),
            generation ?? new MemberKeyGeneration(1),
            Enumerable.Range(0, VaultProtocol.NonceBytes).Select(x => (byte)x).ToArray());

        return MemberVaultMetadataCiphertext.Create(
            scope,
            metadataRevision,
            header,
            Enumerable.Range(0, 48).Select(x => (byte)(x + (int)revision)).ToArray());
    }

    internal static MemberWrappedVaultKey CreateMemberKey(
        VaultScope scope,
        Guid memberId,
        MemberKeyGeneration? generation = null,
        VaultKeyVersion? keyVersion = null) =>
        MemberWrappedVaultKey.Create(
            scope,
            memberId,
            VaultProtocol.CurrentVersion,
            VaultProtocol.AlgorithmSuite,
            keyVersion ?? new VaultKeyVersion(1),
            generation ?? new MemberKeyGeneration(1),
            new MemberRecipientKeyVersion(1),
            Enumerable.Range(0, VaultProtocol.FingerprintBytes).Select(x => (byte)x).ToArray(),
            Enumerable.Range(0, 120).Select(x => (byte)(x + 1)).ToArray());

    internal static VaultKeyMaterialEnvelope[] CreateKeyMaterial(
        VaultScope scope,
        MemberKeyGeneration generation,
        VaultKeyEpoch epoch,
        ulong revision = 1) =>
        [
            CreateKeyMaterialEnvelope(scope, VaultKeyMaterialKind.DiscoveryKey, revision,
                epoch.VdkVersion.Value, generation, epoch.VaultKeyVersion, 1),
            CreateKeyMaterialEnvelope(scope, VaultKeyMaterialKind.AgentMessagePrivateKey, revision,
                epoch.AgentMessageKeyVersion.Value, generation, epoch.VaultKeyVersion, 2),
            CreateKeyMaterialEnvelope(scope, VaultKeyMaterialKind.ManifestSigningPrivateKey, revision,
                epoch.ManifestSigningKeyVersion.Value, generation, epoch.VaultKeyVersion, 3),
        ];

    private static VaultKeyMaterialEnvelope CreateKeyMaterialEnvelope(
        VaultScope scope,
        VaultKeyMaterialKind kind,
        ulong revision,
        uint keyVersion,
        MemberKeyGeneration generation,
        VaultKeyVersion wrappingKeyVersion,
        int offset) => VaultKeyMaterialEnvelope.Create(
        scope,
        kind,
        revision,
        keyVersion,
        generation,
        wrappingKeyVersion,
        new VaultKeyMaterialHeader(
            VaultProtocol.CurrentVersion,
            VaultProtocol.AlgorithmSuite,
            VaultProtocol.VaultResourceKind,
            kind == VaultKeyMaterialKind.DiscoveryKey
                ? VaultProtocol.VaultDiscoveryKeyProjectionKind
                : VaultProtocol.VaultPrivateKeyProjectionKind,
            revision,
            keyVersion,
            generation,
            Enumerable.Range(0, VaultProtocol.NonceBytes).Select(x => (byte)(x + offset)).ToArray()),
        Enumerable.Range(0, 48).Select(x => (byte)(x + offset)).ToArray());
}
