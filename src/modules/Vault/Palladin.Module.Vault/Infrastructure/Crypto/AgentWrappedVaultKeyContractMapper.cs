using Palladin.Core.Cryptography;
using System.Globalization;
using Microsoft.AspNetCore.WebUtilities;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Infrastructure.Crypto;

internal static class AgentWrappedVaultKeyContractMapper
{
    internal static AgentWrappedVaultKey ToDomain(
        AgentWrappedVaultKeyContract contract,
        Guid organizationId,
        Guid vaultId,
        Guid grantId,
        Guid agentId,
        uint agentAccessEpoch,
        uint vaultKeyVersion,
        uint recipientAgentKeyVersion,
        byte[] recipientAgentKeyFingerprint,
        string recipientAgentKeySuiteId,
        uint expectedSigningKeyVersion,
        byte[] expectedSigningKeyFingerprint,
        byte[] expectedSigningPublicKey)
    {
        var descriptor = contract.WrappedVaultKey.Descriptor;
        if (descriptor.ProtocolVersion != VaultProtocol.CurrentVersion
            || !string.Equals(descriptor.WrapperSuiteId, recipientAgentKeySuiteId, StringComparison.Ordinal)
            || descriptor.Purpose != KeyWrapperPurposeContract.AgentVaultKey
            || descriptor.Scope.OrganizationId != organizationId
            || descriptor.Scope.VaultId != vaultId
            || descriptor.Scope.EntryId is not null
            || descriptor.Scope.GrantOrRequestId != grantId
            || descriptor.Scope.AgentId != agentId
            || descriptor.Scope.MemberId is not null
            || descriptor.ResourceRevision != agentAccessEpoch.ToString(CultureInfo.InvariantCulture)
            || descriptor.WrappedKeyVersion != vaultKeyVersion
            || descriptor.MemberKeyGeneration is not null
            || descriptor.RecipientKeyKind != RecipientKeyKindContract.AgentEncryption
            || descriptor.RecipientKeyVersion != recipientAgentKeyVersion
            || descriptor.ParentDescriptorHash is not null)
        {
            throw new DomainException("Agent Vault-key wrapper descriptor is invalid or stale.");
        }

        var package = VaultEnvelopeContractMapper.DecodeCanonicalBase64Url(contract.EncodedSealedVaultKeyPackage);
        KeyWrapperSuites.ResolveForSeal(descriptor.WrapperSuiteId).ValidatePackage(package);
        var fingerprint = WebEncoders.Base64UrlDecode(descriptor.RecipientFingerprint);
        if (!fingerprint.AsSpan().SequenceEqual(recipientAgentKeyFingerprint))
        {
            throw new DomainException("Agent Vault-key wrapper recipient is invalid or stale.");
        }

        AgentWrappedVaultKeyCryptoValidator.ValidateProducer(
            contract,
            expectedSigningKeyVersion,
            expectedSigningKeyFingerprint,
            expectedSigningPublicKey);

        return AgentWrappedVaultKey.Create(
            organizationId,
            vaultId,
            grantId,
            agentId,
            agentAccessEpoch,
            descriptor.ProtocolVersion,
            descriptor.WrapperSuiteId,
            descriptor.WrappedKeyVersion,
            descriptor.RecipientKeyVersion,
            fingerprint,
            package,
            contract.VaultSigningKeyVersion,
            VaultEnvelopeContractMapper.DecodeCanonicalBase64Url(contract.VaultSigningKeyFingerprint),
            VaultEnvelopeContractMapper.DecodeCanonicalBase64Url(contract.ProducerSignature));
    }

    internal static AgentWrappedVaultKeyContract ToContract(AgentWrappedVaultKey value) => new(
        new WrappedKeyContract(
            new KeyWrapperDescriptorContract(
                value.ProtocolVersion,
                value.WrapperSuiteId,
                KeyWrapperPurposeContract.AgentVaultKey,
                new EnvelopeScopeContract(
                    value.OrganizationId,
                    value.VaultId,
                    GrantOrRequestId: value.GrantId,
                    AgentId: value.AgentId),
                value.AgentAccessEpoch.ToString(CultureInfo.InvariantCulture),
                value.VaultKeyVersion.Value,
                null,
                RecipientKeyKindContract.AgentEncryption,
                value.RecipientAgentKeyVersion.Value,
                WebEncoders.Base64UrlEncode(value.RecipientAgentKeyFingerprint),
                null),
            WebEncoders.Base64UrlEncode(value.EncodedSealedVaultKeyPackage)),
        value.VaultSigningKeyVersion.Value,
        WebEncoders.Base64UrlEncode(value.VaultSigningKeyFingerprint),
        WebEncoders.Base64UrlEncode(value.ProducerSignature));
}
