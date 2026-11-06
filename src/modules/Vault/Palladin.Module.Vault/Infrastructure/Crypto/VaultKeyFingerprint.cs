using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Palladin.Core.Cryptography;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Crypto;

internal enum VaultKeyKind : ushort
{
    AgentEncryption = 1,
    AgentSigning = 2,
    VaultSigning = 3,
    VaultAgentMessage = 4,
    MemberEncryption = 5,
    OrganizationApproverMessage = 6,
    OrganizationApproverSigning = 7,
}

internal static class RecipientKeyFingerprint
{
    internal static byte[] Compute(string suiteId, VaultKeyKind keyKind, byte[] rawPublicKey) =>
        KeyWrapperSuites.ResolveForOpen(suiteId).Fingerprint((ushort)keyKind, rawPublicKey);
}

// Frozen vault-v2 fingerprint for 32-byte keys whose scheme is fixed by signed manifests and immutable contracts.
internal static class VaultKeyFingerprint
{
    private static readonly byte[] DomainPrefix = Encoding.ASCII.GetBytes("PLDNV2FP");

    internal static byte[] Compute(byte[] rawPublicKey, VaultKeyKind keyKind)
    {
        if (rawPublicKey.Length != 32)
        {
            throw new DomainException($"{keyKind} public keys must use exactly 32 raw bytes.");
        }

        Span<byte> preimage = stackalloc byte[DomainPrefix.Length + sizeof(ushort) + sizeof(ushort) + 32];
        DomainPrefix.CopyTo(preimage);
        BinaryPrimitives.WriteUInt16BigEndian(preimage[DomainPrefix.Length..], VaultProtocol.CurrentVersion);
        BinaryPrimitives.WriteUInt16BigEndian(
            preimage[(DomainPrefix.Length + sizeof(ushort))..],
            (ushort)keyKind);
        rawPublicKey.CopyTo(preimage[(DomainPrefix.Length + (2 * sizeof(ushort)))..]);
        return SHA256.HashData(preimage);
    }
}
