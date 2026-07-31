using NodaTime;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Shared;

namespace Palladin.Tests.Unit.Modules.Vault;

public sealed class VaultKeyRotationTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 7, 25, 12, 0);

    [Fact]
    public void When_FullRotationIsCreated_Then_TargetsAdvanceExactlyOnce()
    {
        // Given
        var vault = CreateVault();

        // When
        var rotation = VaultKeyRotation.Create(
            Guid.NewGuid(),
            vault,
            VaultKeyRotationCause.ManualSecurityRotation,
            VaultKeyRotationScope.VaultKey
            | VaultKeyRotationScope.Vdk
            | VaultKeyRotationScope.AgentMessage
            | VaultKeyRotationScope.ManifestSigning,
            Guid.NewGuid(),
            Now);

        // Then
        rotation.BaseMemberKeyGeneration.ShouldBe(new MemberKeyGeneration(1));
        rotation.TargetMemberKeyGeneration.ShouldBe(new MemberKeyGeneration(2));
        rotation.TargetKeyEpoch.ShouldBe(new VaultKeyEpoch(
            new VaultKeyVersion(2),
            new VdkVersion(2),
            new AgentMessageKeyVersion(2),
            new ManifestSigningKeyVersion(2)));
        rotation.Status.ShouldBe(VaultKeyRotationStatus.PendingClient);
    }

    [Fact]
    public void When_AnotherMemberOwnsAnActiveLease_Then_ClaimFailsClosed()
    {
        // Given
        var rotation = CreateRotation();
        rotation.Claim(Guid.NewGuid(), Guid.NewGuid(), Now, Duration.FromMinutes(5));

        // When
        Action action = () => rotation.Claim(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Now + Duration.FromMinutes(1),
            Duration.FromMinutes(5));

        // Then
        action.ShouldThrow<VaultKeyRotationLeaseConflictException>();
    }

    [Fact]
    public void When_LeaseExpires_Then_AnotherMemberGetsANewFence()
    {
        // Given
        var rotation = CreateRotation();
        var firstToken = Guid.NewGuid();
        rotation.Claim(Guid.NewGuid(), firstToken, Now, Duration.FromMinutes(5));
        var nextMember = Guid.NewGuid();
        var nextToken = Guid.NewGuid();

        // When
        rotation.Claim(nextMember, nextToken, Now + Duration.FromMinutes(5), Duration.FromMinutes(5));

        // Then
        rotation.LeaseOwnerId.ShouldBe(nextMember);
        rotation.FencingToken.ShouldBe(nextToken);
        rotation.LeaseRevision.ShouldBe(2UL);
    }

    [Fact]
    public void When_StaleFenceAttemptsToFinalize_Then_CommitPreparationFailsClosed()
    {
        // Given
        var rotation = CreateRotation();
        var memberId = Guid.NewGuid();
        var staleToken = Guid.NewGuid();
        rotation.Claim(memberId, staleToken, Now, Duration.FromMinutes(5));
        rotation.Claim(memberId, Guid.NewGuid(), Now + Duration.FromMinutes(1), Duration.FromMinutes(5));

        // When
        var action = () => rotation.MarkReady(memberId, staleToken, Now + Duration.FromMinutes(2));

        // Then
        action.ShouldThrow<VaultKeyRotationFenceException>();
        rotation.Status.ShouldBe(VaultKeyRotationStatus.Preparing);
    }

    [Fact]
    public void When_RotationIsCommitted_Then_LeaseMaterialIsRemoved()
    {
        // Given
        var rotation = CreateRotation();
        var memberId = Guid.NewGuid();
        var fencingToken = Guid.NewGuid();
        rotation.Claim(memberId, fencingToken, Now, Duration.FromMinutes(5));
        rotation.MarkReady(memberId, fencingToken, Now + Duration.FromMinutes(1));

        // When
        rotation.MarkCommitted(Now + Duration.FromMinutes(2));

        // Then
        rotation.Status.ShouldBe(VaultKeyRotationStatus.Committed);
        rotation.CommittedAt.ShouldBe(Now + Duration.FromMinutes(2));
        rotation.LeaseOwnerId.ShouldBeNull();
        rotation.FencingToken.ShouldBeNull();
        rotation.LeaseExpiresAt.ShouldBeNull();
    }

    [Fact]
    public void When_PreparedBatchIsRetriedExactly_Then_ItIsIdempotent()
    {
        var rotation = CreateRotation();
        var memberId = Guid.NewGuid();
        var fencingToken = Guid.NewGuid();
        rotation.Claim(memberId, fencingToken, Now, Duration.FromMinutes(5));
        var subjectId = Guid.NewGuid();
        var item = VaultKeyRotationPreparedItem.Create(rotation,
            VaultKeyRotationPreparedItemKind.EntryKey, subjectId, 1, 1, [1, 2, 3], Now);

        rotation.Prepare(item, memberId, fencingToken, Now).ShouldBeTrue();
        rotation.Prepare(VaultKeyRotationPreparedItem.Create(rotation,
                VaultKeyRotationPreparedItemKind.EntryKey, subjectId, 1, 1, [1, 2, 3], Now),
            memberId, fencingToken, Now).ShouldBeFalse();
        rotation.PreparedItems.ShouldHaveSingleItem();
    }

    [Fact]
    public void When_AgentRotationPayloadRoundTrips_Then_SignedInstantIsPreservedExactly()
    {
        var organizationId = Guid.NewGuid();
        var vaultId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var envelope = new AgentVaultDiscoveryEnvelopeContract(
            2, organizationId, vaultId, agentId, 2, 1, 1, "fingerprint", "wrapped", "2", "signature");
        var manifest = new VaultManifestContract(
            2, 1, organizationId, vaultId, agentId,
            "agent-x", "agent-sign", "vault-sign", "vault-sign-fingerprint", 2,
            "vault-message", "vault-message-fingerprint", 2, 2,
            "wrapped-digest", "2", Now, 2, "signature");
        var payload = new RotationAgentDiscoveryContract(agentId, envelope, manifest);

        var decoded = VaultKeyRotationPayloadCodec.Decode<RotationAgentDiscoveryContract>(
            VaultKeyRotationPayloadCodec.Encode(payload));

        decoded.ShouldBe(payload);
        decoded.Manifest.IssuedAt.ShouldBe(Now);
    }

    private static VaultKeyRotation CreateRotation() => VaultKeyRotation.Create(
        Guid.NewGuid(),
        CreateVault(),
        VaultKeyRotationCause.ManualSecurityRotation,
        VaultKeyRotationScope.Vdk,
        Guid.NewGuid(),
        Now);

    private static Palladin.Module.Vault.Domain.Vault CreateVault()
    {
        var organizationId = Guid.NewGuid();
        var vaultId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        var scope = new VaultScope(organizationId, vaultId);
        var generation = new MemberKeyGeneration(1);
        var epoch = new VaultKeyEpoch(
            new VaultKeyVersion(1),
            new VdkVersion(1),
            new AgentMessageKeyVersion(1),
            new ManifestSigningKeyVersion(1));
        var revision = new MetadataRevision(1);
        var metadata = MemberVaultMetadataCiphertext.Create(
            scope,
            revision,
            MemberVaultMetadataHeader.Create(
                VaultProtocol.CurrentVersion,
                VaultProtocol.AlgorithmSuite,
                VaultProtocol.VaultResourceKind,
                VaultProtocol.MemberVaultMetadataProjectionKind,
                revision,
                epoch.VaultKeyVersion,
                generation,
                new byte[VaultProtocol.NonceBytes]),
            new byte[120]);
        var wrappedKey = MemberWrappedVaultKey.Create(
            scope,
            memberId,
            VaultProtocol.CurrentVersion,
            VaultProtocol.AlgorithmSuite,
            epoch.VaultKeyVersion,
            generation,
            new MemberRecipientKeyVersion(1),
            new byte[VaultProtocol.FingerprintBytes],
            new byte[120]);

        return Palladin.Module.Vault.Domain.Vault.Create(
            vaultId,
            organizationId,
            memberId,
            "test actor",
            metadata,
            generation,
            epoch,
            wrappedKey,
            CreateKeyMaterial(scope, generation, epoch),
            CreatePublicKey(VaultPublicKeyKind.AgentMessageX25519, epoch.AgentMessageKeyVersion.Value),
            CreatePublicKey(VaultPublicKeyKind.ManifestSigningEd25519, epoch.ManifestSigningKeyVersion.Value),
            Now);
    }

    private static ValidatedVaultPublicKey CreatePublicKey(VaultPublicKeyKind kind, uint version)
    {
        var key = Enumerable.Repeat(kind == VaultPublicKeyKind.AgentMessageX25519 ? (byte)0x31 : (byte)0x41, 32).ToArray();
        var fingerprintKind = kind == VaultPublicKeyKind.AgentMessageX25519
            ? VaultKeyKind.VaultMessageX25519
            : VaultKeyKind.VaultSigningEd25519;
        return new ValidatedVaultPublicKey(kind, version, key, VaultKeyFingerprint.Compute(key, fingerprintKind));
    }

    private static VaultKeyMaterialEnvelope[] CreateKeyMaterial(
        VaultScope scope,
        MemberKeyGeneration generation,
        VaultKeyEpoch epoch) =>
        [
            CreateKeyMaterialEnvelope(scope, VaultKeyMaterialKind.DiscoveryKey, epoch.VdkVersion.Value, generation, epoch.VaultKeyVersion),
            CreateKeyMaterialEnvelope(scope, VaultKeyMaterialKind.AgentMessagePrivateKey, epoch.AgentMessageKeyVersion.Value, generation, epoch.VaultKeyVersion),
            CreateKeyMaterialEnvelope(scope, VaultKeyMaterialKind.ManifestSigningPrivateKey, epoch.ManifestSigningKeyVersion.Value, generation, epoch.VaultKeyVersion),
        ];

    private static VaultKeyMaterialEnvelope CreateKeyMaterialEnvelope(
        VaultScope scope,
        VaultKeyMaterialKind kind,
        uint keyVersion,
        MemberKeyGeneration generation,
        VaultKeyVersion wrappingKeyVersion) => VaultKeyMaterialEnvelope.Create(
        scope,
        kind,
        1,
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
            1,
            keyVersion,
            generation,
            new byte[VaultProtocol.NonceBytes]),
        new byte[120]);
}
