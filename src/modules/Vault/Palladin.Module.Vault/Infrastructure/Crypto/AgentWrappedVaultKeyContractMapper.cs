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
        uint expectedSigningKeyVersion,
        byte[] expectedSigningKeyFingerprint,
        byte[] expectedSigningPublicKey)
    {
        var descriptor = contract.WrappedVaultKey.Descriptor;
        if (descriptor.ProtocolVersion != VaultProtocol.CurrentVersion
            || !string.Equals(descriptor.WrapperSuiteId, X25519SealedBoxContract.SuiteId, StringComparison.Ordinal)
            || descriptor.Purpose != X25519WrapperPurposeContract.AgentVaultKey
            || descriptor.Scope.OrganizationId != organizationId
            || descriptor.Scope.VaultId != vaultId
            || descriptor.Scope.EntryId is not null
            || descriptor.Scope.GrantOrRequestId != grantId
            || descriptor.Scope.AgentId != agentId
            || descriptor.Scope.MemberId is not null
            || descriptor.ResourceRevision != agentAccessEpoch.ToString(CultureInfo.InvariantCulture)
            || descriptor.WrappedKeyVersion != vaultKeyVersion
            || descriptor.MemberKeyGeneration is not null
            || descriptor.RecipientKeyKind != X25519RecipientKeyKindContract.AgentX25519
            || descriptor.RecipientKeyVersion != recipientAgentKeyVersion
            || descriptor.ParentDescriptorHash is not null)
        {
            throw new DomainException("Agent Vault-key wrapper descriptor is invalid or stale.");
        }

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
            WebEncoders.Base64UrlDecode(contract.EncodedSealedVaultKeyPackage),
            contract.VaultSigningKeyVersion,
            VaultEnvelopeContractMapper.DecodeCanonicalBase64Url(contract.VaultSigningKeyFingerprint),
            VaultEnvelopeContractMapper.DecodeCanonicalBase64Url(contract.ProducerSignature));
    }

    internal static AgentWrappedVaultKeyContract ToContract(AgentWrappedVaultKey value) => new(
        new X25519WrappedKeyContract(
            new X25519WrapperDescriptorContract(
                value.ProtocolVersion,
                value.WrapperSuiteId,
                X25519WrapperPurposeContract.AgentVaultKey,
                new EnvelopeScopeContract(
                    value.OrganizationId,
                    value.VaultId,
                    GrantOrRequestId: value.GrantId,
                    AgentId: value.AgentId),
                value.AgentAccessEpoch.ToString(CultureInfo.InvariantCulture),
                value.VaultKeyVersion.Value,
                null,
                X25519RecipientKeyKindContract.AgentX25519,
                value.RecipientAgentKeyVersion.Value,
                WebEncoders.Base64UrlEncode(value.RecipientAgentKeyFingerprint),
                null),
            WebEncoders.Base64UrlEncode(value.EncodedSealedVaultKeyPackage)),
        value.VaultSigningKeyVersion.Value,
        WebEncoders.Base64UrlEncode(value.VaultSigningKeyFingerprint),
        WebEncoders.Base64UrlEncode(value.ProducerSignature));
}
