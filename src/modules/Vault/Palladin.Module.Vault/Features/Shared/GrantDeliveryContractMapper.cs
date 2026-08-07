using System.Globalization;
using Microsoft.AspNetCore.WebUtilities;
using Palladin.Core.Types;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

internal static class GrantDeliveryContractMapper
{
    internal static GrantEntryEnvelopeContract ToContract(
        CredentialDeliveryResult.Granted value,
        Guid organizationId,
        Guid vaultId,
        Guid grantId,
        Guid agentId,
        Guid entryId,
        GrantMethods approvedMethods)
    {
        var commitment = EnvelopeDescriptorCodec.ComputeFieldSetCommitment(value.FieldIds);
        var descriptor = new EnvelopeDescriptorContract<GrantEnvelopeBindingContract>(
                value.ProtocolVersion,
                value.CryptoSuiteId,
                EnvelopePurposeContract.GrantPayload,
                new EnvelopeScopeContract(organizationId, vaultId, entryId, grantId, agentId),
                value.GrantEnvelopeRevision.ToString(CultureInfo.InvariantCulture),
                value.GrantKeyVersion,
                value.MemberKeyGeneration,
                new GrantEnvelopeBindingContract(
                    value.EntryRevision.ToString(CultureInfo.InvariantCulture),
                    value.WrapperSuiteId,
                    value.RecipientAgentKeyVersion,
                    WebEncoders.Base64UrlEncode(value.AgentKeyFingerprint),
                    (ushort)approvedMethods,
                    (ushort)value.DeliveryPolicy,
                    WebEncoders.Base64UrlEncode(commitment),
                    value.EnvelopeExpiresAt,
                    value.EnvelopeRemainingUses));
        var domainDescriptor = new Domain.EnvelopeDescriptor(
            descriptor.ProtocolVersion, new Domain.CryptoSuiteId(descriptor.CryptoSuiteId),
            Domain.EnvelopePurpose.GrantPayload,
            new Domain.EnvelopeScope(organizationId, vaultId, entryId, grantId, agentId),
            value.GrantEnvelopeRevision, value.GrantKeyVersion, value.MemberKeyGeneration,
            new Domain.GrantEnvelopeBinding(value.EntryRevision, value.WrapperSuiteId,
                value.RecipientAgentKeyVersion, value.AgentKeyFingerprint, (ushort)approvedMethods,
                (ushort)value.DeliveryPolicy, commitment, value.EnvelopeExpiresAt?.ToUnixTimeSeconds(),
                value.EnvelopeExpiresAt is null ? null : checked((uint)(value.EnvelopeExpiresAt.Value
                    - NodaTime.Instant.FromUnixTimeSeconds(value.EnvelopeExpiresAt.Value.ToUnixTimeSeconds())).TotalNanoseconds),
                value.EnvelopeRemainingUses is null ? null : checked((uint)value.EnvelopeRemainingUses)));
        var parentHash = X25519WrapperContextCodec.ComputeParentDescriptorHash(domainDescriptor);
        return new GrantEntryEnvelopeContract(
            descriptor,
            WebEncoders.Base64UrlEncode(value.EncodedSuitePayload),
            new X25519WrappedKeyContract(
                new X25519WrapperDescriptorContract(value.ProtocolVersion, value.WrapperSuiteId,
                    X25519WrapperPurposeContract.GrantDek,
                    descriptor.Scope, descriptor.ResourceRevision, value.GrantKeyVersion,
                    value.MemberKeyGeneration, X25519RecipientKeyKindContract.AgentX25519,
                    value.RecipientAgentKeyVersion, WebEncoders.Base64UrlEncode(value.AgentKeyFingerprint),
                    WebEncoders.Base64UrlEncode(parentHash)),
                WebEncoders.Base64UrlEncode(value.AgentWrappedGrantDek)),
            value.FieldIds);
    }
}
