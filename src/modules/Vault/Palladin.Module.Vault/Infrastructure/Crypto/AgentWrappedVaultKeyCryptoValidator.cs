using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using NSec.Cryptography;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Infrastructure.Crypto;

internal static class AgentWrappedVaultKeyCryptoValidator
{
    private static readonly byte[] SignaturePrefix =
        Encoding.ASCII.GetBytes("PLDNV2SIG:AGENT-WRAPPED-VAULT-KEY:");

    internal static void ValidateProducer(
        AgentWrappedVaultKeyContract contract,
        uint expectedSigningKeyVersion,
        byte[] expectedSigningKeyFingerprint,
        byte[] expectedSigningPublicKey)
    {
        var signingFingerprint = VaultEnvelopeContractMapper.DecodeCanonicalBase64Url(
            contract.VaultSigningKeyFingerprint);
        var signature = VaultEnvelopeContractMapper.DecodeCanonicalBase64Url(contract.ProducerSignature);
        if (contract.VaultSigningKeyVersion != expectedSigningKeyVersion
            || signingFingerprint.Length != VaultProtocol.FingerprintBytes
            || signature.Length != 64
            || expectedSigningPublicKey.Length != 32
            || !CryptographicOperations.FixedTimeEquals(
                signingFingerprint,
                expectedSigningKeyFingerprint))
        {
            throw new DomainException("Agent Vault-key wrapper producer binding is stale or invalid.");
        }

        var input = BuildSignatureInput(contract);
        var key = PublicKey.Import(
            SignatureAlgorithm.Ed25519,
            expectedSigningPublicKey,
            KeyBlobFormat.RawPublicKey);
        if (!SignatureAlgorithm.Ed25519.Verify(key, input, signature))
        {
            throw new DomainException("Agent Vault-key wrapper producer signature is invalid.");
        }
    }

    internal static byte[] BuildSignatureInput(AgentWrappedVaultKeyContract contract)
    {
        var canonical = CanonicalizeUnsigned(contract);
        var input = new byte[SignaturePrefix.Length + sizeof(ushort) + canonical.Length];
        SignaturePrefix.CopyTo(input, 0);
        BinaryPrimitives.WriteUInt16BigEndian(input.AsSpan(SignaturePrefix.Length), VaultProtocol.CurrentVersion);
        canonical.CopyTo(input, SignaturePrefix.Length + sizeof(ushort));
        return input;
    }

    internal static byte[] CanonicalizeUnsigned(AgentWrappedVaultKeyContract contract)
    {
        var descriptor = contract.WrappedVaultKey.Descriptor;
        var output = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = false,
        });
        writer.WriteStartObject();
        writer.WriteString("vaultSigningKeyFingerprint", contract.VaultSigningKeyFingerprint);
        writer.WriteNumber("vaultSigningKeyVersion", contract.VaultSigningKeyVersion);
        writer.WritePropertyName("wrappedVaultKey");
        writer.WriteStartObject();
        writer.WritePropertyName("descriptor");
        writer.WriteStartObject();
        if (descriptor.MemberKeyGeneration is null) writer.WriteNull("memberKeyGeneration");
        else writer.WriteNumber("memberKeyGeneration", descriptor.MemberKeyGeneration.Value);
        if (descriptor.ParentDescriptorHash is null) writer.WriteNull("parentDescriptorHash");
        else writer.WriteString("parentDescriptorHash", descriptor.ParentDescriptorHash);
        writer.WriteNumber("protocolVersion", descriptor.ProtocolVersion);
        writer.WriteNumber("purpose", (ushort)descriptor.Purpose);
        writer.WriteString("recipientFingerprint", descriptor.RecipientFingerprint);
        writer.WriteNumber("recipientKeyKind", (ushort)descriptor.RecipientKeyKind);
        writer.WriteNumber("recipientKeyVersion", descriptor.RecipientKeyVersion);
        writer.WriteString("resourceRevision", descriptor.ResourceRevision);
        writer.WritePropertyName("scope");
        writer.WriteStartObject();
        WriteGuidOrNull(writer, "agentId", descriptor.Scope.AgentId);
        WriteGuidOrNull(writer, "entryId", descriptor.Scope.EntryId);
        WriteGuidOrNull(writer, "grantOrRequestId", descriptor.Scope.GrantOrRequestId);
        WriteGuidOrNull(writer, "memberId", descriptor.Scope.MemberId);
        writer.WriteString("organizationId", descriptor.Scope.OrganizationId.ToString("D"));
        writer.WriteString("vaultId", descriptor.Scope.VaultId.ToString("D"));
        writer.WriteEndObject();
        writer.WriteNumber("wrappedKeyVersion", descriptor.WrappedKeyVersion);
        writer.WriteString("wrapperSuiteId", descriptor.WrapperSuiteId);
        writer.WriteEndObject();
        writer.WriteString("encodedSealedKeyPackage", contract.EncodedSealedVaultKeyPackage);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        return output.WrittenSpan.ToArray();
    }

    private static void WriteGuidOrNull(Utf8JsonWriter writer, string name, Guid? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, value.Value.ToString("D"));
    }
}
