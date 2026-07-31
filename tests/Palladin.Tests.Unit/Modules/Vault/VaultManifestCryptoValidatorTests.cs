using NodaTime;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using NSec.Cryptography;
using Palladin.Core.Types;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Shared;
using VaultAgent = Palladin.Module.Vault.Domain.Agent;

namespace Palladin.Tests.Unit.Modules.Vault;

public sealed class VaultManifestCryptoValidatorTests
{
    private static readonly Guid OrganizationId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid VaultId = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid AgentId = Guid.Parse("55555555-5555-4555-8555-555555555555");

    [Fact]
    public void When_FrozenSignedManifestIsValid_Then_AcceptsEveryIdentityAndDigestBinding()
    {
        // Given
        var (envelope, manifest, agent) = CreateFixture();

        // When
        var provisioning = VaultManifestCryptoValidator.Validate(envelope, manifest, agent);

        // Then
        provisioning.ManifestRevision.Value.ShouldBe(14UL);
        provisioning.VdkVersion.Value.ShouldBe(6u);
        provisioning.Manifest.Signature.Length.ShouldBe(64);
    }

    [Fact]
    public void When_SignatureIsChanged_Then_FailsClosed()
    {
        // Given
        var (envelope, manifest, agent) = CreateFixture();
        var changedSignature = $"A{envelope.ManifestSignature[1..]}";

        // When
        var action = () => VaultManifestCryptoValidator.Validate(
            envelope with { ManifestSignature = changedSignature },
            manifest with { Signature = changedSignature },
            agent);

        // Then
        action.ShouldThrow<DomainException>().Message.ShouldContain("signature is invalid");
    }

    [Fact]
    public void When_AgentIdentityKeyIsSubstituted_Then_FailsClosed()
    {
        // Given
        var (envelope, manifest, _) = CreateFixture();
        var now = SystemClock.Instance.GetCurrentInstant();
        var substitutedAgent = VaultAgent.Create(
            AgentId,
            OrganizationId,
            AgentStatus.Active,
            Convert.ToBase64String(new byte[32]),
            1,
            Convert.ToBase64String(Convert.FromHexString("0F57FDC1EB05F7DF4A144FE572D805C83E811287B7C6AF8143DF8202DE9B019E")),
            "runtime",
            null,
            null,
            1,
            now,
            now);

        // When
        var action = () => VaultManifestCryptoValidator.Validate(envelope, manifest, substitutedAgent);

        // Then
        action.ShouldThrow<DomainException>().Message.ShouldContain("cryptographic binding is invalid");
    }

    [Fact]
    public void When_RecipientKeyVersionDoesNotMatchAgentReplica_Then_FailsClosed()
    {
        // Given
        var (envelope, manifest, agent) = CreateFixture();
        var staleEnvelope = envelope with
        {
            WrappedVdk = envelope.WrappedVdk with
            {
                Descriptor = envelope.WrappedVdk.Descriptor with { RecipientKeyVersion = 2 },
            },
        };

        // When
        var action = () => VaultManifestCryptoValidator.Validate(staleEnvelope, manifest, agent);

        // Then
        action.ShouldThrow<DomainException>().Message.ShouldContain("cryptographic binding is invalid");
    }

    [Fact]
    public void When_WrapperRecipientFingerprintDoesNotMatchAgentKey_Then_FailsClosed()
    {
        // Given
        var (envelope, manifest, agent) = CreateFixture();
        var changed = envelope with
        {
            WrappedVdk = envelope.WrappedVdk with
            {
                Descriptor = envelope.WrappedVdk.Descriptor with
                {
                    RecipientFingerprint = Encode(RandomNumberGenerator.GetBytes(32)),
                },
            },
        };

        // When
        var action = () => VaultManifestCryptoValidator.Validate(changed, manifest, agent);

        // Then
        action.ShouldThrow<DomainException>().Message.ShouldContain("cryptographic binding is invalid");
    }

    [Fact]
    public void When_ManifestRequiresDifferentRuntimeProtocol_Then_FailsClosed()
    {
        // Given
        var (envelope, manifest, agent) = CreateFixture();
        var incompatibleManifest = manifest with { MinimumAgentRuntimeProtocol = 3 };

        // When
        var action = () => VaultManifestCryptoValidator.Validate(envelope, incompatibleManifest, agent);

        // Then
        action.ShouldThrow<DomainException>().Message.ShouldContain("cryptographic binding is invalid");
    }

    [Fact]
    public void When_IssuedAtExceedsMicrosecondPrecision_Then_FailsClosedBeforePersistence()
    {
        // Given
        var (envelope, manifest, agent) = CreateFixture();
        var nonCanonicalManifest = manifest with { IssuedAt = manifest.IssuedAt + Duration.FromTicks(1) };

        // When
        var action = () => VaultManifestCryptoValidator.Validate(envelope, nonCanonicalManifest, agent);

        // Then
        action.ShouldThrow<DomainException>().Message.ShouldContain("microsecond precision");
    }

    private static (AgentVaultDiscoveryEnvelopeContract Envelope, VaultManifestContract Manifest, VaultAgent Agent) CreateFixture()
    {
        var agentEncryptionPublicKey = RandomNumberGenerator.GetBytes(32);
        var agentSigningPublicKey = RandomNumberGenerator.GetBytes(32);
        var recipientFingerprint = Encode(VaultKeyFingerprint.Compute(agentEncryptionPublicKey, VaultKeyKind.AgentX25519));
        var wrappedVdk = RandomNumberGenerator.GetBytes(120);
        using var vaultSigningKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        });
        var vaultSigningPublicKey = vaultSigningKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        var vaultMessagePublicKey = RandomNumberGenerator.GetBytes(32);
        const uint vdkVersion = 6;
        const string revision = "14";
        var wrapper = new X25519WrappedKeyContract(
            new X25519WrapperDescriptorContract(2, "palladin-x25519-sealed-box-v1",
                X25519WrapperPurposeContract.AgentDiscoveryVdk,
                new EnvelopeScopeContract(OrganizationId, VaultId, AgentId: AgentId),
                vdkVersion.ToString(), vdkVersion, null,
                X25519RecipientKeyKindContract.AgentX25519, 1, recipientFingerprint, null),
            Encode(wrappedVdk));
        var envelope = new AgentVaultDiscoveryEnvelopeContract(
            2, OrganizationId, VaultId, AgentId, vdkVersion, wrapper, revision, string.Empty);
        var manifest = new VaultManifestContract(
            2, "palladin-vault-xchacha-v1", "palladin-x25519-sealed-box-v1", "palladin-ed25519-v1",
            OrganizationId, VaultId, AgentId, recipientFingerprint,
            Encode(VaultKeyFingerprint.Compute(agentSigningPublicKey, VaultKeyKind.AgentEd25519)),
            Encode(vaultSigningPublicKey),
            Encode(VaultKeyFingerprint.Compute(vaultSigningPublicKey, VaultKeyKind.VaultSigningEd25519)), 9,
            Encode(vaultMessagePublicKey),
            Encode(VaultKeyFingerprint.Compute(vaultMessagePublicKey, VaultKeyKind.VaultMessageX25519)), 4,
            vdkVersion, Encode(ComputeWrappedVdkDigest(wrappedVdk)), revision,
            Instant.FromUtc(2026, 7, 17, 6, 0) + Duration.FromTicks(1_234_560), 2, string.Empty);
        var signatureInput = CreateSignatureInput(manifest);
        var signature = Encode(SignatureAlgorithm.Ed25519.Sign(vaultSigningKey, signatureInput));
        manifest = manifest with { Signature = signature };
        envelope = envelope with { ManifestSignature = signature };
        var now = SystemClock.Instance.GetCurrentInstant();
        var agent = VaultAgent.Create(
            AgentId,
            OrganizationId,
            AgentStatus.Active,
            Convert.ToBase64String(agentEncryptionPublicKey),
            1,
            Convert.ToBase64String(agentSigningPublicKey),
            "runtime",
            null,
            null,
            1,
            now,
            now);
        return (envelope, manifest, agent);
    }

    private static byte[] CreateSignatureInput(VaultManifestContract manifest)
    {
        var prefix = Encoding.ASCII.GetBytes("PLDNV2SIG:VAULT-MANIFEST:");
        var canonical = VaultManifestCryptoValidator.CanonicalizeUnsigned(manifest);
        var input = new byte[prefix.Length + sizeof(ushort) + canonical.Length];
        prefix.CopyTo(input, 0);
        BinaryPrimitives.WriteUInt16BigEndian(input.AsSpan(prefix.Length), manifest.ProtocolVersion);
        canonical.CopyTo(input, prefix.Length + sizeof(ushort));
        return input;
    }

    private static byte[] ComputeWrappedVdkDigest(byte[] wrappedVdk)
    {
        var prefix = Encoding.ASCII.GetBytes("PLDNV2DG:AGENT-WRAPPED-VDK:");
        var input = new byte[prefix.Length + sizeof(ushort) + wrappedVdk.Length];
        prefix.CopyTo(input, 0);
        BinaryPrimitives.WriteUInt16BigEndian(input.AsSpan(prefix.Length), 2);
        wrappedVdk.CopyTo(input, prefix.Length + sizeof(ushort));
        return SHA256.HashData(input);
    }

    private static string Encode(byte[] value) => WebEncoders.Base64UrlEncode(value);
}
