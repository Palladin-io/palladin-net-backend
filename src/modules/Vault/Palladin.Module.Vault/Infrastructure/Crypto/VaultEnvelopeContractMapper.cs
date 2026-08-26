using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Infrastructure.Crypto;

internal static class VaultEnvelopeContractMapper
{
    internal static ValidatedVaultPublicKey ToDomain(
        VaultPublicKeyContract contract,
        VaultPublicKeyKindContract expectedKind,
        uint expectedVersion)
    {
        if (contract.ProtocolVersion != VaultProtocol.CurrentVersion
            || contract.KeyKind != expectedKind
            || contract.KeyVersion != expectedVersion
            || contract.KeyVersion == 0)
            throw new DomainException("Vault public-key metadata does not match the key epoch.");
        var expectedScheme = expectedKind == VaultPublicKeyKindContract.AgentMessageX25519
            ? "palladin-x25519-v1"
            : "palladin-ed25519-v1";
        var fingerprintKind = expectedKind == VaultPublicKeyKindContract.AgentMessageX25519
            ? VaultKeyKind.VaultMessageX25519
            : VaultKeyKind.VaultSigningEd25519;
        if (!string.Equals(contract.SchemeId, expectedScheme, StringComparison.Ordinal))
            throw new DomainException("Vault public-key scheme is not supported.");
        var publicKey = DecodeCanonicalBase64Url(contract.EncodedPublicKey);
        var fingerprint = DecodeCanonicalBase64Url(contract.Fingerprint);
        if (publicKey.Length != 32 || fingerprint.Length != VaultProtocol.FingerprintBytes
            || !CryptographicOperations.FixedTimeEquals(fingerprint,
                VaultKeyFingerprint.Compute(publicKey, fingerprintKind)))
            throw new DomainException("Vault public-key fingerprint is invalid.");
        return new ValidatedVaultPublicKey(
            expectedKind == VaultPublicKeyKindContract.AgentMessageX25519
                ? VaultPublicKeyKind.AgentMessageX25519
                : VaultPublicKeyKind.ManifestSigningEd25519,
            contract.KeyVersion, publicKey, fingerprint);
    }

    internal static VaultPublicKeyContract ToAgentMessagePublicKeyContract(Domain.Vault vault) =>
        ToPublicKeyContract(vault, VaultPublicKeyKindContract.AgentMessageX25519,
            vault.CurrentKeyEpoch.AgentMessageKeyVersion.Value, vault.AgentMessagePublicKey,
            vault.AgentMessageKeyFingerprint);

    internal static VaultPublicKeyContract ToManifestSigningPublicKeyContract(Domain.Vault vault) =>
        ToPublicKeyContract(vault, VaultPublicKeyKindContract.ManifestSigningEd25519,
            vault.CurrentKeyEpoch.ManifestSigningKeyVersion.Value, vault.ManifestSigningPublicKey,
            vault.ManifestSigningKeyFingerprint);

    private static VaultPublicKeyContract ToPublicKeyContract(
        Domain.Vault vault, VaultPublicKeyKindContract kind, uint version, byte[] publicKey, byte[] fingerprint) =>
        new(vault.ProtocolVersion,
            kind == VaultPublicKeyKindContract.AgentMessageX25519 ? "palladin-x25519-v1" : "palladin-ed25519-v1",
            kind, version, WebEncoders.Base64UrlEncode(publicKey), WebEncoders.Base64UrlEncode(fingerprint));

    internal static EntryRevision ToEntryRevision(string value) => new(ParseUInt64(value));

    internal static MemberVaultMetadataCiphertext ToDomain(MemberVaultMetadataEnvelopeContract contract)
    {
        var descriptor = ToDomain(contract.Descriptor, new EmptyEnvelopeBinding());
        Validate(descriptor, EnvelopePurpose.MemberVaultMetadata);
        var payload = DecodeXChaChaPayload(contract.EncodedSuitePayload, descriptor.Purpose,
            VaultProtocol.MaximumMetadataCiphertextBytes);
        var revision = new MetadataRevision(descriptor.ResourceRevision);
        return MemberVaultMetadataCiphertext.Create(
            ToVaultScope(descriptor.Scope), revision,
            MemberVaultMetadataHeader.Create(2, 1, VaultProtocol.VaultResourceKind,
                VaultProtocol.MemberVaultMetadataProjectionKind, revision,
                new VaultKeyVersion(descriptor.KeyVersion), ToGeneration(descriptor), payload.Nonce),
            payload.Ciphertext);
    }

    internal static MemberWrappedVaultKey ToDomain(MemberVaultKeyEnvelopeContract contract)
    {
        var wrapper = contract.WrappedVaultKey.Descriptor;
        var context = ToWrapperContext(wrapper);
        if (context.Purpose != X25519WrapperPurpose.MemberVaultKey
            || context.Scope.MemberId != contract.MemberId
            || context.ResourceRevision != contract.VkVersion
            || context.WrappedKeyVersion != contract.VkVersion)
            throw new DomainException("The Member Vault-key wrapper descriptor is invalid.");
        _ = X25519WrapperContextCodec.Encode(context);
        var package = DecodeCanonicalBase64Url(contract.WrappedVaultKey.EncodedSealedKeyPackage);
        WrappedKeyPackageContract.ValidatePackage(wrapper.WrapperSuiteId, package);
        return MemberWrappedVaultKey.Create(new VaultScope(contract.OrganizationId, contract.VaultId),
            contract.MemberId, wrapper.ProtocolVersion, wrapper.WrapperSuiteId, new VaultKeyVersion(contract.VkVersion),
            new MemberKeyGeneration(contract.MemberKeyGeneration),
            new MemberRecipientKeyVersion(contract.RecipientMemberKeyVersion),
            DecodeCanonicalBase64Url(contract.RecipientMemberKeyFingerprint), package);
    }

    internal static VaultKeyEpoch ToDomain(VaultKeyEpochContract contract) => new(
        new VaultKeyVersion(contract.VaultKeyVersion), new VdkVersion(contract.VdkVersion),
        new AgentMessageKeyVersion(contract.AgentMessageKeyVersion),
        new ManifestSigningKeyVersion(contract.ManifestSigningKeyVersion));

    internal static VaultKeyMaterialEnvelope ToDomain(VaultDiscoveryKeyEnvelopeContract contract)
    {
        var binding = new VaultKeyEnvelopeBinding(contract.Descriptor.Binding.WrappingVaultKeyVersion);
        return ToVaultKeyMaterial(contract.Descriptor, contract.EncodedSuitePayload,
            EnvelopePurpose.VaultDiscoveryKey, VaultKeyMaterialKind.DiscoveryKey, binding);
    }

    internal static VaultKeyMaterialEnvelope ToDomain(VaultPrivateKeyEnvelopeContract contract)
    {
        var purpose = (EnvelopePurpose)contract.Descriptor.Purpose;
        var kind = purpose switch
        {
            EnvelopePurpose.VaultAgentMessagePrivateKey => VaultKeyMaterialKind.AgentMessagePrivateKey,
            EnvelopePurpose.VaultManifestSigningPrivateKey => VaultKeyMaterialKind.ManifestSigningPrivateKey,
            _ => throw new DomainException("Unknown Vault private-key envelope purpose."),
        };
        var binding = new VaultKeyEnvelopeBinding(contract.Descriptor.Binding.WrappingVaultKeyVersion);
        return ToVaultKeyMaterial(contract.Descriptor, contract.EncodedSuitePayload, purpose, kind, binding);
    }

    internal static MemberIndexCiphertext ToDomain(MemberIndexEnvelopeContract contract)
    {
        var descriptor = ToDomain(contract.Descriptor, new EmptyEnvelopeBinding());
        Validate(descriptor, EnvelopePurpose.MemberIndex);
        var payload = DecodeXChaChaPayload(contract.EncodedSuitePayload, descriptor.Purpose,
            VaultProtocol.MaximumMemberIndexCiphertextBytes);
        return MemberIndexCiphertext.Create(ToEntryScope(descriptor.Scope),
            new MemberIndexRevision(descriptor.ResourceRevision), ToEntryHeader(descriptor, payload.Nonce),
            payload.Ciphertext);
    }

    internal static MemberSecretCiphertext ToDomain(MemberSecretEnvelopeContract contract)
    {
        var operation = contract.Descriptor.Binding.Operation;
        var descriptor = ToDomain(contract.Descriptor, new MemberSecretEnvelopeBinding((ushort)operation));
        Validate(descriptor, EnvelopePurpose.MemberSecret);
        var payload = DecodeXChaChaPayload(contract.EncodedSuitePayload, descriptor.Purpose,
            VaultProtocol.MaximumMemberSecretCiphertextBytes);
        return MemberSecretCiphertext.Create(ToEntryScope(descriptor.Scope),
            new EntryRevision(descriptor.ResourceRevision), operation, ToEntryHeader(descriptor, payload.Nonce),
            payload.Ciphertext);
    }

    internal static AgentDiscoveryCiphertext ToDomain(AgentDiscoveryEnvelopeContract contract)
    {
        var descriptor = ToDomain(contract.Descriptor, new EmptyEnvelopeBinding());
        Validate(descriptor, EnvelopePurpose.AgentDiscovery);
        var payload = DecodeXChaChaPayload(contract.EncodedSuitePayload, descriptor.Purpose,
            VaultProtocol.MaximumAgentDiscoveryCiphertextBytes);
        return AgentDiscoveryCiphertext.Create(ToEntryScope(descriptor.Scope),
            new AgentDiscoveryRevision(descriptor.ResourceRevision), new VdkVersion(descriptor.KeyVersion),
            ToEntryHeader(descriptor, payload.Nonce), payload.Ciphertext);
    }

    internal static Domain.VaultEntryKey ToDomain(VaultEntryKeyContract contract)
    {
        var binding = new VaultKeyEnvelopeBinding(contract.Descriptor.Binding.WrappingVaultKeyVersion);
        var descriptor = ToDomain(contract.Descriptor, binding);
        Validate(descriptor, EnvelopePurpose.EntryDekByVaultKey);
        var payload = DecodeXChaChaPayload(contract.EncodedSuitePayload, descriptor.Purpose,
            VaultProtocol.MaximumWrappedEntryKeyBytes);
        return Domain.VaultEntryKey.Create(ToEntryScope(descriptor.Scope),
            new EntryKeyWrapperRevision(descriptor.ResourceRevision), new EntryKeyVersion(descriptor.KeyVersion),
            ToGeneration(descriptor), new VaultKeyVersion(binding.WrappingVaultKeyVersion),
            ToEntryHeader(descriptor, payload.Nonce), payload.Ciphertext);
    }

    internal static MemberVaultMetadataEnvelopeContract ToContract(MemberVaultMetadataCiphertext value) => new(
        Descriptor<EmptyEnvelopeBindingContract>(value.Scope.OrganizationId, value.Scope.VaultId, null,
            EnvelopePurpose.MemberVaultMetadata, value.MetadataRevision.Value, value.Header.KeyVersion.Value,
            value.Header.MemberKeyGeneration.Value, new EmptyEnvelopeBindingContract()),
        EncodePayload(value.Header.Nonce, value.Ciphertext));

    internal static MemberVaultKeyEnvelopeContract ToContract(MemberWrappedVaultKey value) => new(
        new X25519WrappedKeyContract(
            new X25519WrapperDescriptorContract(value.ProtocolVersion, value.WrapperSuiteId,
                X25519WrapperPurposeContract.MemberVaultKey,
                new EnvelopeScopeContract(value.Scope.OrganizationId, value.Scope.VaultId, MemberId: value.MemberId),
                value.VaultKeyVersion.Value.ToString(CultureInfo.InvariantCulture), value.VaultKeyVersion.Value,
                value.MemberKeyGeneration.Value, X25519RecipientKeyKindContract.MemberX25519,
                value.RecipientKeyVersion.Value, WebEncoders.Base64UrlEncode(value.RecipientKeyFingerprint), null),
            WebEncoders.Base64UrlEncode(value.SealedVaultKeyPackage)));

    internal static VaultKeyEpochContract ToContract(VaultKeyEpoch value) => new(
        value.VaultKeyVersion.Value, value.VdkVersion.Value, value.AgentMessageKeyVersion.Value,
        value.ManifestSigningKeyVersion.Value);

    internal static VaultDiscoveryKeyEnvelopeContract ToDiscoveryKeyContract(VaultKeyMaterialEnvelope value)
    {
        if (value.Kind != VaultKeyMaterialKind.DiscoveryKey)
            throw new DomainException("Only a Vault Discovery key can use this contract.");
        return new VaultDiscoveryKeyEnvelopeContract(
            Descriptor(value, EnvelopePurpose.VaultDiscoveryKey), WebEncoders.Base64UrlEncode(value.EncodedSuitePayload));
    }

    internal static VaultPrivateKeyEnvelopeContract ToPrivateKeyContract(VaultKeyMaterialEnvelope value)
    {
        var purpose = value.Kind switch
        {
            VaultKeyMaterialKind.AgentMessagePrivateKey => EnvelopePurpose.VaultAgentMessagePrivateKey,
            VaultKeyMaterialKind.ManifestSigningPrivateKey => EnvelopePurpose.VaultManifestSigningPrivateKey,
            _ => throw new DomainException("Only a Vault private key can use this contract."),
        };
        return new VaultPrivateKeyEnvelopeContract(Descriptor(value, purpose),
            WebEncoders.Base64UrlEncode(value.EncodedSuitePayload));
    }

    internal static MemberIndexEnvelopeContract ToContract(MemberIndexCiphertext value) => new(
        Descriptor<EmptyEnvelopeBindingContract>(value.Scope.OrganizationId, value.Scope.VaultId,
            value.Scope.EntryId, EnvelopePurpose.MemberIndex, value.Revision.Value, value.Header.KeyVersion,
            value.Header.MemberKeyGeneration.Value, new EmptyEnvelopeBindingContract()),
        EncodePayload(value.Header.Nonce, value.Ciphertext));

    internal static MemberSecretEnvelopeContract ToContract(MemberSecretCiphertext value) => new(
        Descriptor(value.Scope.OrganizationId, value.Scope.VaultId, value.Scope.EntryId,
            EnvelopePurpose.MemberSecret, value.Revision.Value, value.Header.KeyVersion,
            value.Header.MemberKeyGeneration.Value, new MemberSecretEnvelopeBindingContract(value.Operation)),
        EncodePayload(value.Header.Nonce, value.Ciphertext));

    internal static AgentDiscoveryEnvelopeContract ToContract(AgentDiscoveryCiphertext value) => new(
        Descriptor<EmptyEnvelopeBindingContract>(value.Scope.OrganizationId, value.Scope.VaultId,
            value.Scope.EntryId, EnvelopePurpose.AgentDiscovery, value.Revision.Value, value.VdkVersion.Value,
            value.Header.MemberKeyGeneration.Value, new EmptyEnvelopeBindingContract()),
        EncodePayload(value.Header.Nonce, value.Ciphertext));

    internal static VaultEntryKeyContract ToContract(Domain.VaultEntryKey value) => new(
        Descriptor(value.OrganizationId, value.VaultId, value.EntryId, EnvelopePurpose.EntryDekByVaultKey,
            value.WrapperRevision.Value, value.KeyVersion.Value, value.MemberKeyGeneration.Value,
            new VaultKeyEnvelopeBindingContract(value.WrappingKeyVersion.Value)),
        WebEncoders.Base64UrlEncode(value.EncodedSuitePayload));

    internal static AgentVaultDiscoveryEnvelopeContract ToContract(AgentVaultDiscoveryEnvelope value) => new(
        value.ProtocolVersion, value.OrganizationId, value.VaultId, value.AgentId, value.VdkVersion.Value,
        new X25519WrappedKeyContract(
            new X25519WrapperDescriptorContract(value.ProtocolVersion, X25519SealedBoxContract.SuiteId,
                X25519WrapperPurposeContract.AgentDiscoveryVdk,
                new EnvelopeScopeContract(value.OrganizationId, value.VaultId, AgentId: value.AgentId),
                value.VdkVersion.Value.ToString(CultureInfo.InvariantCulture), value.VdkVersion.Value,
                null, X25519RecipientKeyKindContract.AgentX25519, value.RecipientAgentKeyVersion.Value,
                WebEncoders.Base64UrlEncode(value.RecipientAgentKeyFingerprint), null),
            WebEncoders.Base64UrlEncode(value.AgentWrappedVdk)),
        value.ManifestRevision.Value.ToString(CultureInfo.InvariantCulture),
        WebEncoders.Base64UrlEncode(value.ManifestSignature));

    internal static VaultManifestContract ToManifestContract(AgentVaultDiscoveryEnvelope value) => new(
        value.ProtocolVersion, CryptoSuiteId.XChaCha20Poly1305V1, X25519SealedBoxContract.SuiteId,
        "palladin-ed25519-v1", value.OrganizationId, value.VaultId, value.AgentId,
        WebEncoders.Base64UrlEncode(value.AgentX25519Fingerprint), WebEncoders.Base64UrlEncode(value.AgentEd25519Fingerprint),
        WebEncoders.Base64UrlEncode(value.VaultSigningPublicKey), WebEncoders.Base64UrlEncode(value.VaultSigningKeyFingerprint),
        value.ManifestSigningKeyVersion.Value, WebEncoders.Base64UrlEncode(value.VaultAgentMessagePublicKey),
        WebEncoders.Base64UrlEncode(value.VaultAgentMessageKeyFingerprint), value.AgentMessageKeyVersion.Value,
        value.VdkVersion.Value, WebEncoders.Base64UrlEncode(value.AgentWrappedVdkDigest),
        value.ManifestRevision.Value.ToString(CultureInfo.InvariantCulture), value.IssuedAt,
        value.MinimumAgentRuntimeProtocol, WebEncoders.Base64UrlEncode(value.ManifestSignature));

    private static VaultKeyMaterialEnvelope ToVaultKeyMaterial<T>(
        EnvelopeDescriptorContract<T> contract, string encodedPayload, EnvelopePurpose purpose,
        VaultKeyMaterialKind kind, VaultKeyEnvelopeBinding binding)
    {
        var descriptor = ToDomain(contract, binding);
        Validate(descriptor, purpose);
        var payload = DecodeXChaChaPayload(encodedPayload, purpose, VaultProtocol.MaximumVaultKeyMaterialCiphertextBytes);
        return VaultKeyMaterialEnvelope.Create(ToVaultScope(descriptor.Scope), kind, descriptor.ResourceRevision,
            descriptor.KeyVersion, ToGeneration(descriptor), new VaultKeyVersion(binding.WrappingVaultKeyVersion),
            new VaultKeyMaterialHeader(2, 1, VaultProtocol.VaultResourceKind,
                purpose == EnvelopePurpose.VaultDiscoveryKey ? VaultProtocol.VaultDiscoveryKeyProjectionKind
                    : VaultProtocol.VaultPrivateKeyProjectionKind,
                descriptor.ResourceRevision, descriptor.KeyVersion, ToGeneration(descriptor), payload.Nonce),
            payload.Ciphertext);
    }

    private static EnvelopeDescriptorContract<VaultKeyEnvelopeBindingContract> Descriptor(
        VaultKeyMaterialEnvelope value, EnvelopePurpose purpose) =>
        Descriptor(value.OrganizationId, value.VaultId, null, purpose, value.Revision, value.KeyVersion,
            value.MemberKeyGeneration.Value, new VaultKeyEnvelopeBindingContract(value.WrappingKeyVersion.Value));

    private static EnvelopeDescriptorContract<T> Descriptor<T>(Guid organizationId, Guid vaultId, Guid? entryId,
        EnvelopePurpose purpose, ulong revision, uint keyVersion, uint generation, T binding) => new(
        2, CryptoSuiteId.XChaCha20Poly1305V1, (EnvelopePurposeContract)purpose,
        new EnvelopeScopeContract(organizationId, vaultId, entryId),
        revision.ToString(CultureInfo.InvariantCulture), keyVersion, generation, binding);

    private static EnvelopeDescriptor ToDomain<T>(EnvelopeDescriptorContract<T> value, EnvelopeBinding binding) => new(
        value.ProtocolVersion, new CryptoSuiteId(value.CryptoSuiteId), (EnvelopePurpose)value.Purpose,
        new EnvelopeScope(value.Scope.OrganizationId, value.Scope.VaultId, value.Scope.EntryId,
            value.Scope.GrantOrRequestId, value.Scope.AgentId, value.Scope.MemberId),
        ParseUInt64(value.ResourceRevision), value.KeyVersion, value.MemberKeyGeneration, binding);

    private static void Validate(EnvelopeDescriptor descriptor, EnvelopePurpose purpose)
    {
        if (descriptor.Purpose != purpose) throw new DomainException("Envelope purpose does not match its contract.");
        _ = EnvelopeDescriptorCodec.Encode(descriptor);
    }

    private static EntryEnvelopeHeader ToEntryHeader(EnvelopeDescriptor descriptor, byte[] nonce) =>
        EntryEnvelopeHeader.Create(2, 1, VaultProtocol.EntryResourceKind, Projection(descriptor.Purpose),
            descriptor.ResourceRevision, descriptor.KeyVersion, ToGeneration(descriptor), nonce,
            Projection(descriptor.Purpose));

    private static ushort Projection(EnvelopePurpose purpose) => purpose switch
    {
        EnvelopePurpose.MemberIndex => VaultProtocol.MemberIndexProjectionKind,
        EnvelopePurpose.MemberSecret => VaultProtocol.MemberSecretProjectionKind,
        EnvelopePurpose.AgentDiscovery => VaultProtocol.AgentDiscoveryProjectionKind,
        EnvelopePurpose.EntryDekByVaultKey => VaultProtocol.WrappedKeyProjectionKind,
        _ => throw new DomainException("Purpose is not an Entry envelope."),
    };

    private static MemberKeyGeneration ToGeneration(EnvelopeDescriptor value) =>
        new(value.MemberKeyGeneration ?? throw new DomainException("Member-key generation is required."));
    private static VaultScope ToVaultScope(EnvelopeScope value) => new(value.OrganizationId, value.VaultId);
    private static EntryScope ToEntryScope(EnvelopeScope value) =>
        new(value.OrganizationId, value.VaultId, value.EntryId ?? throw new DomainException("Entry scope is required."));

    internal static (byte[] Nonce, byte[] Ciphertext) DecodeXChaChaPayload(
        string encoded, EnvelopePurpose purpose, int maximumCiphertextBytes)
    {
        var payload = new EncodedSuitePayload(DecodeCanonicalBase64Url(encoded));
        new XChaCha20Poly1305VaultEnvelopeSuite().ValidateEncodedPayload(purpose, payload, maximumCiphertextBytes);
        return (payload.Bytes[..VaultProtocol.NonceBytes], payload.Bytes[VaultProtocol.NonceBytes..]);
    }

    private static string EncodePayload(byte[] nonce, byte[] ciphertext)
    {
        var payload = new byte[nonce.Length + ciphertext.Length];
        nonce.CopyTo(payload, 0); ciphertext.CopyTo(payload, nonce.Length);
        return WebEncoders.Base64UrlEncode(payload);
    }

    internal static ulong ParseUInt64(string value)
    {
        if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed.ToString(CultureInfo.InvariantCulture) != value)
            throw new DomainException("Unsigned 64-bit values must use canonical decimal encoding.");
        return parsed;
    }

    internal static byte[] DecodeCanonicalBase64Url(string value)
    {
        if (string.IsNullOrEmpty(value))
            throw new DomainException("Binary values must use non-empty canonical unpadded base64url encoding.");
        try
        {
            var decoded = WebEncoders.Base64UrlDecode(value);
            if (WebEncoders.Base64UrlEncode(decoded) != value)
                throw new DomainException("Binary values must use canonical unpadded base64url encoding.");
            return decoded;
        }
        catch (FormatException)
        {
            throw new DomainException("Binary values must use canonical unpadded base64url encoding.");
        }
    }

    internal static X25519WrapperContext ToWrapperContext(X25519WrapperDescriptorContract value) => new(
        (X25519WrapperPurpose)value.Purpose,
        new EnvelopeScope(value.Scope.OrganizationId, value.Scope.VaultId, value.Scope.EntryId,
            value.Scope.GrantOrRequestId, value.Scope.AgentId, value.Scope.MemberId),
        ParseUInt64(value.ResourceRevision), value.WrappedKeyVersion, value.MemberKeyGeneration,
        (VaultKeyKind)value.RecipientKeyKind, value.RecipientKeyVersion,
        DecodeCanonicalBase64Url(value.RecipientFingerprint),
        value.ParentDescriptorHash is null ? null : DecodeCanonicalBase64Url(value.ParentDescriptorHash));
}
