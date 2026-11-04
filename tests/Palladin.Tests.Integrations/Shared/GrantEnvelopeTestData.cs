using Microsoft.AspNetCore.WebUtilities;
using Palladin.Core.Cryptography;
using NodaTime;
using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Shared;
using Palladin.Tests.Integrations.Shared.Fakers;

namespace Palladin.Tests.Integrations.Shared;

internal static class GrantEnvelopeTestData
{
    internal static string Suite => KeyWrapperSuites.Current.Id;

    internal static string DefaultAgentPublicKey { get; } =
        Convert.ToBase64String(new byte[KeyWrapperSuites.Current.PublicKeyBytes]);

    internal static Agent Recipient(Guid agentId, string? publicKey = null) =>
        VaultAgentFaker.Create(id: agentId, publicKey: publicKey ?? DefaultAgentPublicKey).Generate();

    internal static EncryptedReasonEnvelopeContract EncryptedReason(
        Guid organizationId,
        Guid vaultId,
        Guid entryId,
        Guid agentId,
        AgentRequestSigning signing,
        string recipientAgentMessageKeyFingerprint,
        GrantMethods methods = GrantMethods.Get,
        Guid? requestId = null,
        uint agentMessageKeyVersion = 1,
        string resourceRevision = "1",
        string signaturePrefix = "PLDNV2SIG:ENCRYPTED-REASON:")
    {
        _ = signaturePrefix;
        var id = requestId ?? Guid.NewGuid();
        var scope = new EnvelopeScopeContract(organizationId, vaultId, entryId, id, agentId);
        var binding = new ReasonEnvelopeBindingContract(Suite,
            agentMessageKeyVersion, recipientAgentMessageKeyFingerprint, (ushort)methods);
        var descriptor = new EnvelopeDescriptorContract<ReasonEnvelopeBindingContract>(
            VaultProtocol.CurrentVersion, CryptoSuiteId.XChaCha20Poly1305V1,
            EnvelopePurposeContract.EncryptedReason, scope, resourceRevision, 1, 1, binding);
        var domainDescriptor = new EnvelopeDescriptor(VaultProtocol.CurrentVersion,
            new CryptoSuiteId(CryptoSuiteId.XChaCha20Poly1305V1), EnvelopePurpose.EncryptedReason,
            new EnvelopeScope(organizationId, vaultId, entryId, id, agentId),
            ulong.Parse(resourceRevision), 1, 1,
            new ReasonEnvelopeBinding(Suite, agentMessageKeyVersion,
                WebEncoders.Base64UrlDecode(recipientAgentMessageKeyFingerprint), (ushort)methods));
        var parentHash = WebEncoders.Base64UrlEncode(KeyWrapperContextCodec.ComputeParentDescriptorHash(domainDescriptor));
        var unsigned = new EncryptedReasonEnvelopeContract(
            descriptor,
            Base64Url(VaultProtocol.NonceBytes + 64),
            new WrappedKeyContract(
                new KeyWrapperDescriptorContract(2, Suite,
                    KeyWrapperPurposeContract.ReasonDek, scope, resourceRevision, 1, 1,
                    RecipientKeyKindContract.VaultAgentMessage, agentMessageKeyVersion,
                    recipientAgentMessageKeyFingerprint, parentHash),
                Base64Url(KeyWrapperSuites.Current.PackageBytes)),
            string.Empty);
        var transcript = EncryptedReasonValidator.BuildSignatureTranscript(
            EnvelopeDescriptorCodec.Encode(domainDescriptor),
            WebEncoders.Base64UrlDecode(unsigned.EncodedSuitePayload),
            Suite,
            WebEncoders.Base64UrlDecode(unsigned.WrappedReasonDek.EncodedSealedKeyPackage));
        return unsigned with { AgentSignature = signing.SignBytesBase64Url(transcript) };
    }

    internal static GrantEntryEnvelopeContract Contract(
        Guid organizationId,
        Guid vaultId,
        Guid grantId,
        Guid entryId,
        string agentPublicKey,
        Instant? expiresAt = null,
        int? remainingUses = null,
        ulong entryRevision = 1,
        ulong envelopeRevision = 1,
        uint grantKeyVersion = 1,
        string[]? fieldIds = null,
        Guid? agentId = null,
        GrantMethods methods = GrantMethods.Get,
        GrantDeliveryPolicy deliveryPolicy = GrantDeliveryPolicy.Standard) =>
        CreateGrant(organizationId, vaultId, grantId, entryId, agentPublicKey, expiresAt, remainingUses,
            entryRevision, envelopeRevision, grantKeyVersion, fieldIds ?? ["username", "password"],
            agentId ?? Guid.Parse("55555555-5555-4555-8555-555555555555"), methods, deliveryPolicy);

    private static GrantEntryEnvelopeContract CreateGrant(Guid organizationId, Guid vaultId, Guid grantId,
        Guid entryId, string agentPublicKey, Instant? expiresAt, int? remainingUses, ulong entryRevision,
        ulong envelopeRevision, uint grantKeyVersion, string[] fieldIds, Guid agentId, GrantMethods methods,
        GrantDeliveryPolicy deliveryPolicy)
    {
        var fingerprint = WebEncoders.Base64UrlEncode(AgentRecipientKey.Fingerprint(Suite, agentPublicKey));
        var scope = new EnvelopeScopeContract(organizationId, vaultId, entryId, grantId, agentId);
        var commitment = WebEncoders.Base64UrlEncode(EnvelopeDescriptorCodec.ComputeFieldSetCommitment(fieldIds));
        var binding = new GrantEnvelopeBindingContract(entryRevision.ToString(), Suite,
            1, fingerprint, (ushort)methods, (ushort)deliveryPolicy, commitment, expiresAt, remainingUses);
        var descriptor = new EnvelopeDescriptorContract<GrantEnvelopeBindingContract>(2,
            CryptoSuiteId.XChaCha20Poly1305V1, EnvelopePurposeContract.GrantPayload, scope,
            envelopeRevision.ToString(), grantKeyVersion, 1, binding);
        var domain = new EnvelopeDescriptor(2, new CryptoSuiteId(CryptoSuiteId.XChaCha20Poly1305V1), EnvelopePurpose.GrantPayload,
            new EnvelopeScope(organizationId, vaultId, entryId, grantId, agentId), envelopeRevision, grantKeyVersion, 1,
            new GrantEnvelopeBinding(entryRevision, Suite, 1,
                WebEncoders.Base64UrlDecode(fingerprint), (ushort)methods,
                (ushort)deliveryPolicy, WebEncoders.Base64UrlDecode(commitment), expiresAt?.ToUnixTimeSeconds(),
                expiresAt is null ? null : (uint)(expiresAt.Value.ToUnixTimeTicks() % NodaConstants.TicksPerSecond * 100),
                remainingUses is null ? null : checked((uint)remainingUses.Value)));
        var parentHash = WebEncoders.Base64UrlEncode(KeyWrapperContextCodec.ComputeParentDescriptorHash(domain));
        return new GrantEntryEnvelopeContract(descriptor, Base64Url(VaultProtocol.NonceBytes + 64),
            new WrappedKeyContract(new KeyWrapperDescriptorContract(2,
                Suite, KeyWrapperPurposeContract.GrantDek, scope,
                envelopeRevision.ToString(), grantKeyVersion, 1, RecipientKeyKindContract.AgentEncryption,
                1, fingerprint, parentHash), Base64Url(KeyWrapperSuites.Current.PackageBytes)), fieldIds);
    }

    internal static GrantEntryScope Scope(
        Guid organizationId,
        Guid vaultId,
        Guid grantId,
        Guid entryId,
        GrantMethods methods = GrantMethods.Get,
        Instant? expiresAt = null,
        int? remainingUses = null,
        ulong entryRevision = 1,
        ulong envelopeRevision = 1,
        uint grantKeyVersion = 1,
        string[]? fieldIds = null,
        Guid? agentId = null,
        string? agentPublicKey = null,
        GrantDeliveryPolicy deliveryPolicy = GrantDeliveryPolicy.Standard)
    {
        var recipientAgentId = agentId ?? Guid.Parse("55555555-5555-4555-8555-555555555555");
        var publicKey = agentPublicKey ?? DefaultAgentPublicKey;
        var contract = Contract(organizationId, vaultId, grantId, entryId,
            publicKey, expiresAt, remainingUses, entryRevision,
            envelopeRevision, grantKeyVersion, fieldIds, recipientAgentId, methods, deliveryPolicy);
        return Palladin.Module.Vault.Infrastructure.Crypto.GrantEnvelopeContractMapper.ToDomain(
            contract, methods, VaultAgentFaker.Create(id: recipientAgentId, publicKey: publicKey).Generate());
    }

    internal static AgentWrappedVaultKey AgentVaultKey(
        Guid organizationId,
        Guid vaultId,
        Guid grantId,
        Guid agentId,
        uint agentAccessEpoch = 1,
        uint vaultKeyVersion = 1,
        uint recipientAgentKeyVersion = 1,
        string? agentPublicKey = null)
    {
        var fingerprint = AgentRecipientKey.Fingerprint(Suite, agentPublicKey ?? DefaultAgentPublicKey);
        return AgentWrappedVaultKey.Create(
            organizationId,
            vaultId,
            grantId,
            agentId,
            agentAccessEpoch,
            VaultProtocol.CurrentVersion,
            Suite,
            vaultKeyVersion,
            recipientAgentKeyVersion,
            fingerprint,
            [.. Enumerable.Range(0, KeyWrapperSuites.Current.PackageBytes).Select(i => (byte)i)]);
    }

    internal static AgentWrappedVaultKeyContract AgentVaultKeyContract(
        Guid organizationId,
        Guid vaultId,
        Guid grantId,
        Guid agentId,
        uint agentAccessEpoch = 1,
        uint vaultKeyVersion = 1,
        uint recipientAgentKeyVersion = 1,
        string? agentPublicKey = null,
        uint vaultSigningKeyVersion = 1,
        bool rotatedSigningKey = false)
    {
        var signingPublicKey = rotatedSigningKey
            ? VaultTrustAnchorFaker.RotatedManifestSigningPublicKey
            : VaultTrustAnchorFaker.ManifestSigningPublicKey;
        var unsigned = AgentWrappedVaultKeyContractMapper.ToContract(AgentWrappedVaultKey.Create(
            organizationId,
            vaultId,
            grantId,
            agentId,
            agentAccessEpoch,
            VaultProtocol.CurrentVersion,
            Suite,
            vaultKeyVersion,
            recipientAgentKeyVersion,
            AgentRecipientKey.Fingerprint(Suite, agentPublicKey ?? DefaultAgentPublicKey),
            [.. Enumerable.Range(0, KeyWrapperSuites.Current.PackageBytes).Select(i => (byte)i)],
            vaultSigningKeyVersion,
            VaultKeyFingerprint.Compute(signingPublicKey, VaultKeyKind.VaultSigning),
            new byte[64]));
        var signature = VaultTrustAnchorFaker.SignWithManifestKey(
            AgentWrappedVaultKeyCryptoValidator.BuildSignatureInput(unsigned),
            rotatedSigningKey);
        return unsigned with { ProducerSignature = WebEncoders.Base64UrlEncode(signature) };
    }
    private static string Base64Url(int length) =>
        WebEncoders.Base64UrlEncode([.. Enumerable.Range(0, length).Select(i => (byte)i)]);
}
