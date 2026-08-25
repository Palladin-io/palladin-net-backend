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

internal static class ScriptExecutionPackageCryptoValidator
{
    private static readonly byte[] SignaturePrefix =
        Encoding.ASCII.GetBytes("PLDNV2SIG:SCRIPT-EXECUTION-PACKAGE:");

    internal static void ValidateProducer(ScriptExecutionPackageContract package, Domain.Vault vault)
    {
        var signingFingerprint = VaultEnvelopeContractMapper.DecodeCanonicalBase64Url(
            package.VaultSigningKeyFingerprint);
        var signature = VaultEnvelopeContractMapper.DecodeCanonicalBase64Url(package.ProducerSignature);
        if (package.VaultSigningKeyVersion != vault.CurrentManifestSigningKeyVersion.Value
            || signingFingerprint.Length != VaultProtocol.FingerprintBytes
            || signature.Length != 64
            || !CryptographicOperations.FixedTimeEquals(
                signingFingerprint,
                vault.ManifestSigningKeyFingerprint))
        {
            throw new DomainException("Script execution package producer binding is stale or invalid.");
        }

        var canonical = CanonicalizeUnsigned(package);
        var input = new byte[SignaturePrefix.Length + sizeof(ushort) + canonical.Length];
        SignaturePrefix.CopyTo(input, 0);
        BinaryPrimitives.WriteUInt16BigEndian(input.AsSpan(SignaturePrefix.Length), 2);
        canonical.CopyTo(input, SignaturePrefix.Length + sizeof(ushort));
        var key = PublicKey.Import(
            SignatureAlgorithm.Ed25519,
            vault.ManifestSigningPublicKey,
            KeyBlobFormat.RawPublicKey);
        if (!SignatureAlgorithm.Ed25519.Verify(key, input, signature))
        {
            throw new DomainException("Script execution package producer signature is invalid.");
        }
    }

    internal static byte[] CanonicalizeUnsigned(ScriptExecutionPackageContract package)
    {
        var output = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = false,
        });
        writer.WriteStartObject();
        writer.WriteNumber("agentAccessEpoch", package.AgentAccessEpoch);
        writer.WriteString("agentId", package.AgentId.ToString("D"));
        writer.WriteNumber("contractVersion", package.ContractVersion);
        writer.WriteString("encodedPackageCiphertext", package.EncodedPackageCiphertext);
        writer.WriteString("grantId", package.GrantId.ToString("D"));
        writer.WriteString("manifestDigest", package.ManifestDigest);
        writer.WriteString("organizationId", package.OrganizationId.ToString("D"));
        writer.WriteString("packageRevision", package.PackageRevision);
        writer.WriteString("recipientAgentKeyFingerprint", package.RecipientAgentKeyFingerprint);
        writer.WriteNumber("recipientAgentKeyVersion", package.RecipientAgentKeyVersion);
        writer.WritePropertyName("scopes");
        writer.WriteStartArray();
        foreach (var scope in package.Scopes)
        {
            writer.WriteStartObject();
            writer.WriteString("entryId", scope.EntryId.ToString("D"));
            writer.WriteString("entryRevision", scope.EntryRevision);
            writer.WriteBoolean("isScript", scope.IsScript);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteString("scriptEntryId", package.ScriptEntryId.ToString("D"));
        writer.WriteString("scriptRevision", package.ScriptRevision);
        writer.WriteString("vaultId", package.VaultId.ToString("D"));
        writer.WriteString("vaultSigningKeyFingerprint", package.VaultSigningKeyFingerprint);
        writer.WriteNumber("vaultSigningKeyVersion", package.VaultSigningKeyVersion);
        writer.WriteEndObject();
        writer.Flush();
        return output.WrittenSpan.ToArray();
    }
}
