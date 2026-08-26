using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using NodaTime;
using NodaTime.Text;
using NSec.Cryptography;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Infrastructure.Crypto;

internal static class VaultManifestCryptoValidator
{
    private static readonly byte[] SignaturePrefix = Encoding.ASCII.GetBytes("PLDNV2SIG:VAULT-MANIFEST:");
    private static readonly byte[] WrappedVdkDigestPrefix = Encoding.ASCII.GetBytes("PLDNV2DG:AGENT-WRAPPED-VDK:");

    internal static ValidatedAgentDiscoveryProvisioning Validate(
        AgentVaultDiscoveryEnvelopeContract envelope,
        VaultManifestContract manifest,
        Agent agent)
    {
        if (!HasCanonicalMicrosecondPrecision(manifest.IssuedAt))
        {
            throw new DomainException("Vault manifest issuedAt must use canonical microsecond precision.");
        }

        var scope = new VaultScope(envelope.OrganizationId, envelope.VaultId);
        var wrappedVdk = VaultEnvelopeContractMapper.DecodeCanonicalBase64Url(envelope.AgentWrappedVdk);
        var manifestSignature = VaultEnvelopeContractMapper.DecodeCanonicalBase64Url(envelope.ManifestSignature);
        var recipientFingerprint = VaultEnvelopeContractMapper.DecodeCanonicalBase64Url(envelope.RecipientAgentKeyFingerprint);
        var wrapperContext = VaultEnvelopeContractMapper.ToWrapperContext(envelope.WrappedVdk.Descriptor);
        if (wrapperContext.Purpose != X25519WrapperPurpose.AgentDiscoveryVdk
            || wrapperContext.Scope != new EnvelopeScope(envelope.OrganizationId, envelope.VaultId,
                AgentId: envelope.AgentId)
            || wrapperContext.ResourceRevision != envelope.VdkVersion
            || wrapperContext.WrappedKeyVersion != envelope.VdkVersion
            || wrapperContext.RecipientKeyKind != VaultKeyKind.AgentX25519
            || wrapperContext.RecipientKeyVersion != envelope.RecipientAgentKeyVersion
            || !CryptographicOperations.FixedTimeEquals(wrapperContext.RecipientFingerprint,
                recipientFingerprint)
            || wrapperContext.ParentDescriptorHash is not null)
            throw new DomainException("Agent Discovery wrapper descriptor is invalid.");
        _ = X25519WrapperContextCodec.Encode(wrapperContext);
        WrappedKeyPackageContract.ValidatePackage(envelope.WrappedVdk.Descriptor.WrapperSuiteId, wrappedVdk);
        var agentX25519Key = DecodeAgentPublicKey(agent.PublicKey, VaultKeyKind.AgentX25519);
        var agentEd25519Key = DecodeAgentPublicKey(agent.SigningPublicKey, VaultKeyKind.AgentEd25519);
        var vaultSigningKey = VaultEnvelopeContractMapper.DecodeCanonicalBase64Url(manifest.VaultSigningPublicKey);
        var vaultMessageKey = VaultEnvelopeContractMapper.DecodeCanonicalBase64Url(manifest.VaultAgentMessagePublicKey);
        var agentX25519Fingerprint = VaultKeyFingerprint.Compute(agentX25519Key, VaultKeyKind.AgentX25519);
        var agentEd25519Fingerprint = VaultKeyFingerprint.Compute(agentEd25519Key, VaultKeyKind.AgentEd25519);
        var vaultSigningFingerprint = VaultKeyFingerprint.Compute(vaultSigningKey, VaultKeyKind.VaultSigningEd25519);
        var vaultMessageFingerprint = VaultKeyFingerprint.Compute(vaultMessageKey, VaultKeyKind.VaultMessageX25519);
        var wrappedVdkDigest = ComputeWrappedVdkDigest(envelope.ProtocolVersion, wrappedVdk);

        ValidateBindings(
            envelope,
            manifest,
            agent,
            manifestSignature,
            recipientFingerprint,
            agentX25519Fingerprint,
            agentEd25519Fingerprint,
            vaultSigningFingerprint,
            vaultMessageFingerprint,
            wrappedVdkDigest);
        VerifySignature(manifest, vaultSigningKey, manifestSignature);

        var revision = new ManifestRevision(VaultEnvelopeContractMapper.ParseUInt64(manifest.ManifestRevision));
        return new ValidatedAgentDiscoveryProvisioning(
            scope,
            envelope.AgentId,
            envelope.ProtocolVersion,
            VaultProtocol.AlgorithmSuite,
            new VdkVersion(envelope.VdkVersion),
            new AgentRecipientKeyVersion(envelope.RecipientAgentKeyVersion),
            recipientFingerprint,
            wrappedVdk,
            revision,
            manifestSignature,
            new ValidatedVaultManifest(
                manifest.ProtocolVersion,
                VaultProtocol.AlgorithmSuite,
                scope,
                manifest.AgentId,
                agentX25519Fingerprint,
                agentEd25519Fingerprint,
                vaultSigningKey,
                vaultSigningFingerprint,
                new ManifestSigningKeyVersion(manifest.ManifestSigningKeyVersion),
                vaultMessageKey,
                vaultMessageFingerprint,
                new AgentMessageKeyVersion(manifest.AgentMessageKeyVersion),
                new VdkVersion(manifest.VdkVersion),
                wrappedVdkDigest,
                revision,
                manifest.IssuedAt,
                manifest.MinimumAgentRuntimeProtocol,
                manifestSignature));
    }

    internal static ValidatedAgentDiscoveryProvisioning ValidateCurrent(
        AgentVaultDiscoveryEnvelopeContract envelope,
        VaultManifestContract manifest,
        Agent agent,
        Domain.Vault vault)
    {
        var provisioning = Validate(envelope, manifest, agent);
        var validatedManifest = provisioning.Manifest;
        if (validatedManifest.Scope != vault.Scope
            || validatedManifest.VdkVersion != vault.CurrentVdkVersion
            || validatedManifest.ManifestSigningKeyVersion != vault.CurrentManifestSigningKeyVersion
            || validatedManifest.AgentMessageKeyVersion != vault.CurrentAgentMessageKeyVersion
            || !CryptographicOperations.FixedTimeEquals(
                validatedManifest.VaultSigningPublicKey,
                vault.ManifestSigningPublicKey)
            || !CryptographicOperations.FixedTimeEquals(
                validatedManifest.VaultSigningKeyFingerprint,
                vault.ManifestSigningKeyFingerprint)
            || !CryptographicOperations.FixedTimeEquals(
                validatedManifest.VaultAgentMessagePublicKey,
                vault.AgentMessagePublicKey)
            || !CryptographicOperations.FixedTimeEquals(
                validatedManifest.VaultAgentMessageKeyFingerprint,
                vault.AgentMessageKeyFingerprint))
        {
            throw new DomainException("Vault manifest does not match the current Vault trust anchors.");
        }

        return provisioning;
    }

    internal static byte[] CanonicalizeUnsigned(VaultManifestContract manifest)
    {
        var output = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = false,
        });
        writer.WriteStartObject();
        writer.WriteString("agentEd25519Fingerprint", manifest.AgentEd25519Fingerprint);
        writer.WriteString("agentId", manifest.AgentId);
        writer.WriteNumber("agentMessageKeyVersion", manifest.AgentMessageKeyVersion);
        writer.WriteString("agentWrappedVdkDigest", manifest.AgentWrappedVdkDigest);
        writer.WriteString("agentX25519Fingerprint", manifest.AgentX25519Fingerprint);
        writer.WriteString("cryptoSuiteId", manifest.CryptoSuiteId);
        writer.WriteString("issuedAt", InstantPattern.ExtendedIso.Format(manifest.IssuedAt));
        writer.WriteString("manifestRevision", manifest.ManifestRevision);
        writer.WriteNumber("manifestSigningKeyVersion", manifest.ManifestSigningKeyVersion);
        writer.WriteNumber("minimumAgentRuntimeProtocol", manifest.MinimumAgentRuntimeProtocol);
        writer.WriteString("organizationId", manifest.OrganizationId);
        writer.WriteNumber("protocolVersion", manifest.ProtocolVersion);
        writer.WriteString("signatureSuiteId", manifest.SignatureSuiteId);
        writer.WriteString("vaultAgentMessageKeyFingerprint", manifest.VaultAgentMessageKeyFingerprint);
        writer.WriteString("vaultAgentMessagePublicKey", manifest.VaultAgentMessagePublicKey);
        writer.WriteString("vaultId", manifest.VaultId);
        writer.WriteString("vaultSigningKeyFingerprint", manifest.VaultSigningKeyFingerprint);
        writer.WriteString("vaultSigningPublicKey", manifest.VaultSigningPublicKey);
        writer.WriteNumber("vdkVersion", manifest.VdkVersion);
        writer.WriteString("wrapperSuiteId", manifest.WrapperSuiteId);
        writer.WriteEndObject();
        writer.Flush();
        return output.WrittenSpan.ToArray();
    }

    internal static byte[] CanonicalizeSigned(VaultManifestContract manifest)
    {
        var output = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = false,
        });
        writer.WriteStartObject();
        writer.WriteString("agentEd25519Fingerprint", manifest.AgentEd25519Fingerprint);
        writer.WriteString("agentId", manifest.AgentId);
        writer.WriteNumber("agentMessageKeyVersion", manifest.AgentMessageKeyVersion);
        writer.WriteString("agentWrappedVdkDigest", manifest.AgentWrappedVdkDigest);
        writer.WriteString("agentX25519Fingerprint", manifest.AgentX25519Fingerprint);
        writer.WriteString("cryptoSuiteId", manifest.CryptoSuiteId);
        writer.WriteString("issuedAt", InstantPattern.ExtendedIso.Format(manifest.IssuedAt));
        writer.WriteString("manifestRevision", manifest.ManifestRevision);
        writer.WriteNumber("manifestSigningKeyVersion", manifest.ManifestSigningKeyVersion);
        writer.WriteNumber("minimumAgentRuntimeProtocol", manifest.MinimumAgentRuntimeProtocol);
        writer.WriteString("organizationId", manifest.OrganizationId);
        writer.WriteNumber("protocolVersion", manifest.ProtocolVersion);
        writer.WriteString("signature", manifest.Signature);
        writer.WriteString("signatureSuiteId", manifest.SignatureSuiteId);
        writer.WriteString("vaultAgentMessageKeyFingerprint", manifest.VaultAgentMessageKeyFingerprint);
        writer.WriteString("vaultAgentMessagePublicKey", manifest.VaultAgentMessagePublicKey);
        writer.WriteString("vaultId", manifest.VaultId);
        writer.WriteString("vaultSigningKeyFingerprint", manifest.VaultSigningKeyFingerprint);
        writer.WriteString("vaultSigningPublicKey", manifest.VaultSigningPublicKey);
        writer.WriteNumber("vdkVersion", manifest.VdkVersion);
        writer.WriteString("wrapperSuiteId", manifest.WrapperSuiteId);
        writer.WriteEndObject();
        writer.Flush();
        return output.WrittenSpan.ToArray();
    }

    private static void ValidateBindings(
        AgentVaultDiscoveryEnvelopeContract envelope,
        VaultManifestContract manifest,
        Agent agent,
        byte[] manifestSignature,
        byte[] recipientFingerprint,
        byte[] agentX25519Fingerprint,
        byte[] agentEd25519Fingerprint,
        byte[] vaultSigningFingerprint,
        byte[] vaultMessageFingerprint,
        byte[] wrappedVdkDigest)
    {
        if (envelope.ProtocolVersion != VaultProtocol.CurrentVersion
            || !string.Equals(envelope.WrappedVdk.Descriptor.WrapperSuiteId,
                X25519SealedBoxContract.SuiteId, StringComparison.Ordinal)
            || manifest.ProtocolVersion != envelope.ProtocolVersion
            || !string.Equals(manifest.CryptoSuiteId, CryptoSuiteId.XChaCha20Poly1305V1, StringComparison.Ordinal)
            || !string.Equals(manifest.WrapperSuiteId, envelope.WrappedVdk.Descriptor.WrapperSuiteId,
                StringComparison.Ordinal)
            || !string.Equals(manifest.SignatureSuiteId, "palladin-ed25519-v1", StringComparison.Ordinal)
            || manifest.OrganizationId != envelope.OrganizationId
            || manifest.VaultId != envelope.VaultId
            || manifest.AgentId != envelope.AgentId
            || manifest.VdkVersion != envelope.VdkVersion
            || manifest.ManifestRevision != envelope.ManifestRevision
            || manifest.Signature != envelope.ManifestSignature
            || manifest.MinimumAgentRuntimeProtocol != VaultProtocol.CurrentVersion
            || envelope.AgentId != agent.Id
            || envelope.OrganizationId != agent.OrganizationId
            || envelope.RecipientAgentKeyVersion != agent.RecipientKeyVersion
            || manifestSignature.Length != 64
            || !recipientFingerprint.AsSpan().SequenceEqual(agentX25519Fingerprint)
            || !DecodeFingerprint(manifest.AgentX25519Fingerprint).AsSpan().SequenceEqual(agentX25519Fingerprint)
            || !DecodeFingerprint(manifest.AgentEd25519Fingerprint).AsSpan().SequenceEqual(agentEd25519Fingerprint)
            || !DecodeFingerprint(manifest.VaultSigningKeyFingerprint).AsSpan().SequenceEqual(vaultSigningFingerprint)
            || !DecodeFingerprint(manifest.VaultAgentMessageKeyFingerprint).AsSpan().SequenceEqual(vaultMessageFingerprint)
            || !DecodeFingerprint(manifest.AgentWrappedVdkDigest).AsSpan().SequenceEqual(wrappedVdkDigest))
        {
            throw new DomainException("Vault manifest identity, version, or cryptographic binding is invalid.");
        }
    }

    private static void VerifySignature(VaultManifestContract manifest, byte[] publicKey, byte[] signature)
    {
        var canonical = CanonicalizeUnsigned(manifest);
        var input = new byte[SignaturePrefix.Length + sizeof(ushort) + canonical.Length];
        SignaturePrefix.CopyTo(input, 0);
        BinaryPrimitives.WriteUInt16BigEndian(input.AsSpan(SignaturePrefix.Length), manifest.ProtocolVersion);
        canonical.CopyTo(input, SignaturePrefix.Length + sizeof(ushort));
        var key = PublicKey.Import(SignatureAlgorithm.Ed25519, publicKey, KeyBlobFormat.RawPublicKey);
        if (!SignatureAlgorithm.Ed25519.Verify(key, input, signature))
        {
            throw new DomainException("Vault manifest signature is invalid.");
        }
    }

    private static byte[] ComputeWrappedVdkDigest(ushort protocolVersion, byte[] wrappedVdk)
    {
        var input = new byte[WrappedVdkDigestPrefix.Length + sizeof(ushort) + wrappedVdk.Length];
        WrappedVdkDigestPrefix.CopyTo(input, 0);
        BinaryPrimitives.WriteUInt16BigEndian(input.AsSpan(WrappedVdkDigestPrefix.Length), protocolVersion);
        wrappedVdk.CopyTo(input, WrappedVdkDigestPrefix.Length + sizeof(ushort));
        return SHA256.HashData(input);
    }

    private static byte[] DecodeAgentPublicKey(string value, VaultKeyKind keyKind)
    {
        try
        {
            var decoded = Convert.FromBase64String(value);
            return decoded.Length == 32
                ? decoded
                : throw new DomainException($"{keyKind} identity must contain exactly 32 raw bytes.");
        }
        catch (FormatException)
        {
            throw new DomainException($"{keyKind} identity must use canonical base64 key encoding.");
        }
    }

    private static byte[] DecodeFingerprint(string value)
    {
        var decoded = VaultEnvelopeContractMapper.DecodeCanonicalBase64Url(value);
        return decoded.Length == VaultProtocol.FingerprintBytes
            ? decoded
            : throw new DomainException("Vault manifest fingerprints and digests must contain exactly 32 bytes.");
    }

    private static bool HasCanonicalMicrosecondPrecision(Instant value)
    {
        var ticks = value.ToUnixTimeTicks();
        return ticks % 10 == 0 && value == Instant.FromUnixTimeTicks(ticks);
    }
}
