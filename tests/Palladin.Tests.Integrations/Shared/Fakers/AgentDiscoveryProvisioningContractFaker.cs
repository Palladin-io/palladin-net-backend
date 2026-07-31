using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using NodaTime;
using NSec.Cryptography;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Shared;

namespace Palladin.Tests.Integrations.Shared.Fakers;

internal sealed record AgentDiscoveryProvisioningFixture(
    string X25519PublicKey,
    AgentRequestSigning RequestSigning,
    ProvisionAgentDiscoveryRequest Request);

internal static class AgentDiscoveryProvisioningContractFaker
{
    private static readonly byte[] SignaturePrefix = Encoding.ASCII.GetBytes("PLDNV2SIG:VAULT-MANIFEST:");
    private static readonly byte[] WrappedVdkDigestPrefix = Encoding.ASCII.GetBytes("PLDNV2DG:AGENT-WRAPPED-VDK:");

    internal static AgentDiscoveryProvisioningFixture Create(
        Guid organizationId,
        Guid vaultId,
        Guid agentId,
        ulong revision = 1)
    {
        var requestSigning = AgentRequestSigning.Generate();
        var x25519PublicKey = AgentFaker.GeneratePublicKey();
        var agentX25519Key = Convert.FromBase64String(x25519PublicKey);
        var agentEd25519Key = Convert.FromBase64String(requestSigning.PublicKeyBase64);
        var wrappedVdk = RandomNumberGenerator.GetBytes(120);
        using var vaultSigningKey = VaultTrustAnchorFaker.CreateManifestSigningKey();
        var vaultSigningPublicKey = vaultSigningKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        var vaultMessagePublicKey = VaultTrustAnchorFaker.AgentMessagePublicKey;
        var manifest = new VaultManifestContract(
            2,
            1,
            organizationId,
            vaultId,
            agentId,
            Encode(VaultKeyFingerprint.Compute(agentX25519Key, VaultKeyKind.AgentX25519)),
            Encode(VaultKeyFingerprint.Compute(agentEd25519Key, VaultKeyKind.AgentEd25519)),
            Encode(vaultSigningPublicKey),
            Encode(VaultKeyFingerprint.Compute(vaultSigningPublicKey, VaultKeyKind.VaultSigningEd25519)),
            1,
            Encode(vaultMessagePublicKey),
            Encode(VaultKeyFingerprint.Compute(vaultMessagePublicKey, VaultKeyKind.VaultMessageX25519)),
            1,
            1,
            Encode(ComputeWrappedVdkDigest(wrappedVdk)),
            revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            TruncateToMicroseconds(SystemClock.Instance.GetCurrentInstant()),
            2,
            string.Empty);
        var signature = Encode(SignatureAlgorithm.Ed25519.Sign(vaultSigningKey, CreateSignatureInput(manifest)));
        manifest = manifest with { Signature = signature };
        var envelope = new AgentVaultDiscoveryEnvelopeContract(
            2,
            organizationId,
            vaultId,
            agentId,
            1,
            1,
            1,
            Encode(VaultKeyFingerprint.Compute(agentX25519Key, VaultKeyKind.AgentX25519)),
            Encode(wrappedVdk),
            revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            signature);
        return new AgentDiscoveryProvisioningFixture(
            x25519PublicKey,
            requestSigning,
            new ProvisionAgentDiscoveryRequest
            {
                VaultId = vaultId,
                AgentId = agentId,
                Envelope = envelope,
                Manifest = manifest,
            });
    }

    private static byte[] CreateSignatureInput(VaultManifestContract manifest)
    {
        var canonical = VaultManifestCryptoValidator.CanonicalizeUnsigned(manifest);
        var input = new byte[SignaturePrefix.Length + sizeof(ushort) + canonical.Length];
        SignaturePrefix.CopyTo(input, 0);
        BinaryPrimitives.WriteUInt16BigEndian(input.AsSpan(SignaturePrefix.Length), manifest.ProtocolVersion);
        canonical.CopyTo(input, SignaturePrefix.Length + sizeof(ushort));
        return input;
    }

    private static byte[] ComputeWrappedVdkDigest(byte[] wrappedVdk)
    {
        var input = new byte[WrappedVdkDigestPrefix.Length + sizeof(ushort) + wrappedVdk.Length];
        WrappedVdkDigestPrefix.CopyTo(input, 0);
        BinaryPrimitives.WriteUInt16BigEndian(input.AsSpan(WrappedVdkDigestPrefix.Length), 2);
        wrappedVdk.CopyTo(input, WrappedVdkDigestPrefix.Length + sizeof(ushort));
        return SHA256.HashData(input);
    }

    private static string Encode(byte[] value) => WebEncoders.Base64UrlEncode(value);

    private static Instant TruncateToMicroseconds(Instant value)
    {
        var ticks = value.ToUnixTimeTicks();
        return Instant.FromUnixTimeTicks(ticks - (ticks % 10));
    }
}
