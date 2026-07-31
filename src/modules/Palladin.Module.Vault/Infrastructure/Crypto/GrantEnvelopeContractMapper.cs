using System.Security.Cryptography;
using NodaTime;
using Palladin.Core.Types;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Infrastructure.Crypto;

internal static class GrantEnvelopeContractMapper
{
    internal static GrantEntryScope ToDomain(
        GrantEntryEnvelopeContract contract,
        GrantMethods methods,
        Guid expectedAgentId)
    {
        if (contract.Descriptor?.Scope is null
            || contract.Descriptor.Binding is null
            || contract.WrappedGrantDek?.Descriptor?.Scope is null)
            throw new DomainException("Grant envelope structure is incomplete.");
        if (expectedAgentId == Guid.Empty || contract.Descriptor.Scope.AgentId != expectedAgentId)
            throw new DomainException("Grant envelope Agent scope does not match the durable grant.");
        var binding = contract.Descriptor.Binding;
        if (binding.RemainingUses is <= 0)
            throw new DomainException("Grant remaining uses must be a positive bounded integer.");
        if (binding.ExpiresAt.HasValue && binding.RemainingUses.HasValue)
            throw new DomainException("Grant envelope cannot bind both expiry policies.");
        if ((GrantMethods)binding.ApprovedMethods != methods)
            throw new DomainException("Grant methods do not match the authenticated envelope binding.");

        var expectedCommitment = EnvelopeDescriptorCodec.ComputeFieldSetCommitment(contract.FieldIds);
        var submittedCommitment = VaultEnvelopeContractMapper.DecodeCanonicalBase64Url(binding.FieldSetCommitment);
        if (!CryptographicOperations.FixedTimeEquals(expectedCommitment, submittedCommitment))
            throw new DomainException("Grant field set does not match its authenticated commitment.");

        var fingerprint = VaultEnvelopeContractMapper.DecodeCanonicalBase64Url(binding.RecipientKeyFingerprint);
        var expirySeconds = binding.ExpiresAt?.ToUnixTimeSeconds();
        uint? expiryNanos = binding.ExpiresAt is null
            ? null
            : checked((uint)(binding.ExpiresAt.Value - Instant.FromUnixTimeSeconds(expirySeconds!.Value)).TotalNanoseconds);
        var descriptor = new EnvelopeDescriptor(
            contract.Descriptor.ProtocolVersion, new CryptoSuiteId(contract.Descriptor.CryptoSuiteId),
            (EnvelopePurpose)contract.Descriptor.Purpose,
            new EnvelopeScope(contract.OrganizationId, contract.VaultId, contract.EntryId,
                contract.GrantId, contract.AgentId),
            VaultEnvelopeContractMapper.ParseUInt64(contract.GrantEnvelopeRevision), contract.GrantKeyVersion,
            contract.MemberKeyGeneration,
            new GrantEnvelopeBinding(
                VaultEnvelopeContractMapper.ParseUInt64(binding.EntryRevision), binding.WrapperSuiteId,
                binding.RecipientKeyVersion, fingerprint, binding.ApprovedMethods, submittedCommitment,
                expirySeconds, expiryNanos, binding.RemainingUses is null ? null : checked((uint)binding.RemainingUses)));
        if (descriptor.Purpose != EnvelopePurpose.GrantPayload)
            throw new DomainException("Grant envelope purpose is invalid.");
        _ = EnvelopeDescriptorCodec.Encode(descriptor);

        var payload = VaultEnvelopeContractMapper.DecodeXChaChaPayload(
            contract.EncodedSuitePayload, descriptor.Purpose, 262_144);
        var wrapperContext = VaultEnvelopeContractMapper.ToWrapperContext(contract.WrappedGrantDek.Descriptor);
        var parentHash = X25519WrapperContextCodec.ComputeParentDescriptorHash(descriptor);
        if (wrapperContext.Purpose != X25519WrapperPurpose.GrantDek
            || wrapperContext.Scope != descriptor.Scope
            || wrapperContext.ResourceRevision != descriptor.ResourceRevision
            || wrapperContext.WrappedKeyVersion != descriptor.KeyVersion
            || wrapperContext.MemberKeyGeneration != descriptor.MemberKeyGeneration
            || wrapperContext.RecipientKeyVersion != binding.RecipientKeyVersion
            || wrapperContext.RecipientKeyKind != VaultKeyKind.AgentX25519
            || !string.Equals(binding.WrapperSuiteId, X25519SealedBoxContract.SuiteId, StringComparison.Ordinal)
            || !CryptographicOperations.FixedTimeEquals(wrapperContext.RecipientFingerprint, fingerprint)
            || wrapperContext.ParentDescriptorHash is null
            || !CryptographicOperations.FixedTimeEquals(wrapperContext.ParentDescriptorHash, parentHash))
            throw new DomainException("Grant wrapper descriptor does not match its parent envelope.");
        _ = X25519WrapperContextCodec.Encode(wrapperContext);
        var wrapper = VaultEnvelopeContractMapper.DecodeCanonicalBase64Url(
            contract.WrappedGrantDek.EncodedSealedKeyPackage);
        X25519SealedBoxContract.ValidatePackage(wrapper);
        var scope = new EntryScope(contract.OrganizationId, contract.VaultId, contract.EntryId);
        var envelope = GrantEntryEnvelope.Create(scope, contract.GrantId, descriptor.ResourceRevision,
            new EntryRevision(VaultEnvelopeContractMapper.ParseUInt64(binding.EntryRevision)),
            descriptor.KeyVersion, new MemberKeyGeneration(contract.MemberKeyGeneration),
            new AgentRecipientKeyVersion(binding.RecipientKeyVersion), payload.Ciphertext, payload.Nonce,
            wrapper, 1, fingerprint, binding.ExpiresAt, binding.RemainingUses);
        return GrantEntryScope.Create(scope, contract.GrantId, methods, contract.FieldIds, envelope);
    }
}
