using System.Buffers.Binary;
using System.Text;
using System.Security.Cryptography;
using NSec.Cryptography;
using Palladin.Core.Types;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Infrastructure.Crypto;

internal static class EncryptedReasonValidator
{
    private static readonly byte[] SignaturePrefix = "PLDNV2SIG:ENCRYPTED-REASON:"u8.ToArray();

    internal static EncryptedReasonEnvelope ToDomain(
        EncryptedReasonEnvelopeContract contract,
        string agentSigningPublicKey,
        byte[] expectedRecipientAgentMessageKeyFingerprint)
    {
        if (contract.Descriptor?.Scope is null
            || contract.Descriptor.Binding is null
            || contract.WrappedReasonDek?.Descriptor?.Scope is null)
            throw new DomainException("Encrypted reason structure is incomplete.");
        var binding = contract.Descriptor.Binding;
        var fingerprint = VaultEnvelopeContractMapper.DecodeCanonicalBase64Url(binding.RecipientKeyFingerprint);
        if (!CryptographicOperations.FixedTimeEquals(fingerprint, expectedRecipientAgentMessageKeyFingerprint)
            || !string.Equals(binding.WrapperSuiteId, X25519SealedBoxContract.SuiteId, StringComparison.Ordinal))
            throw new DomainException("Encrypted reason recipient is invalid.");

        var descriptor = new EnvelopeDescriptor(
            contract.Descriptor.ProtocolVersion, new CryptoSuiteId(contract.Descriptor.CryptoSuiteId),
            (EnvelopePurpose)contract.Descriptor.Purpose,
            new EnvelopeScope(contract.OrganizationId, contract.VaultId, contract.EntryId,
                contract.GrantRequestId, contract.AgentId),
            VaultEnvelopeContractMapper.ParseUInt64(contract.RequestRevision), contract.ReasonKeyVersion,
            contract.MemberKeyGeneration,
            new ReasonEnvelopeBinding(binding.WrapperSuiteId, binding.RecipientKeyVersion,
                fingerprint, binding.RequestedMethods));
        if (descriptor.Purpose != EnvelopePurpose.EncryptedReason)
            throw new DomainException("Encrypted reason purpose is invalid.");
        var descriptorBytes = EnvelopeDescriptorCodec.Encode(descriptor);
        var payload = VaultEnvelopeContractMapper.DecodeXChaChaPayload(
            contract.EncodedSuitePayload, descriptor.Purpose, 4_096);
        var wrapperContext = VaultEnvelopeContractMapper.ToWrapperContext(contract.WrappedReasonDek.Descriptor);
        var parentHash = X25519WrapperContextCodec.ComputeParentDescriptorHash(descriptor);
        if (wrapperContext.Purpose != X25519WrapperPurpose.ReasonDek
            || wrapperContext.Scope != descriptor.Scope
            || wrapperContext.ResourceRevision != descriptor.ResourceRevision
            || wrapperContext.WrappedKeyVersion != descriptor.KeyVersion
            || wrapperContext.MemberKeyGeneration != descriptor.MemberKeyGeneration
            || wrapperContext.RecipientKeyVersion != binding.RecipientKeyVersion
            || wrapperContext.RecipientKeyKind != VaultKeyKind.VaultMessageX25519
            || !string.Equals(binding.WrapperSuiteId, X25519SealedBoxContract.SuiteId, StringComparison.Ordinal)
            || !CryptographicOperations.FixedTimeEquals(wrapperContext.RecipientFingerprint, fingerprint)
            || wrapperContext.ParentDescriptorHash is null
            || !CryptographicOperations.FixedTimeEquals(wrapperContext.ParentDescriptorHash, parentHash))
            throw new DomainException("Encrypted reason wrapper descriptor does not match its parent envelope.");
        _ = X25519WrapperContextCodec.Encode(wrapperContext);
        var wrapper = VaultEnvelopeContractMapper.DecodeCanonicalBase64Url(
            contract.WrappedReasonDek.EncodedSealedKeyPackage);
        WrappedKeyPackageContract.ValidatePackage(binding.WrapperSuiteId, wrapper);

        var signature = VaultEnvelopeContractMapper.DecodeCanonicalBase64Url(contract.AgentSignature);
        var signed = BuildSignatureTranscript(descriptorBytes,
            VaultEnvelopeContractMapper.DecodeCanonicalBase64Url(contract.EncodedSuitePayload), wrapper);
        var publicKey = PublicKey.Import(SignatureAlgorithm.Ed25519,
            Convert.FromBase64String(agentSigningPublicKey), KeyBlobFormat.RawPublicKey);
        if (!SignatureAlgorithm.Ed25519.Verify(publicKey, signed, signature))
            throw new DomainException("Encrypted reason signature is invalid.");

        return EncryptedReasonEnvelope.Create(
            new EntryScope(contract.OrganizationId, contract.VaultId, contract.EntryId),
            contract.GrantRequestId, contract.AgentId, descriptor.ResourceRevision, descriptor.KeyVersion,
            binding.RecipientKeyVersion, contract.MemberKeyGeneration, fingerprint,
            (GrantMethods)binding.RequestedMethods, payload.Ciphertext, payload.Nonce, wrapper, signature);
    }

    internal static byte[] BuildSignatureTranscript(
        byte[] descriptor, byte[] encodedSuitePayload, byte[] encodedWrapper)
    {
        var suite = Encoding.ASCII.GetBytes(X25519SealedBoxContract.SuiteId);
        var result = new byte[SignaturePrefix.Length + 2 + descriptor.Length + encodedSuitePayload.Length
            + 2 + suite.Length + encodedWrapper.Length];
        var offset = 0;
        SignaturePrefix.CopyTo(result, offset); offset += SignaturePrefix.Length;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(offset), VaultProtocol.CurrentVersion); offset += 2;
        descriptor.CopyTo(result, offset); offset += descriptor.Length;
        encodedSuitePayload.CopyTo(result, offset); offset += encodedSuitePayload.Length;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(offset), checked((ushort)suite.Length)); offset += 2;
        suite.CopyTo(result, offset); offset += suite.Length;
        encodedWrapper.CopyTo(result, offset);
        return result;
    }
}
