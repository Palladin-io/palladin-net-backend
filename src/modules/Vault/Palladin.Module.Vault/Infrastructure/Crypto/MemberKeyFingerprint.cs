using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Crypto;

internal static class MemberKeyFingerprint
{
    internal static byte[] Compute(byte[] rawPublicKey)
    {
        if (rawPublicKey.Length != 32)
        {
            throw new DomainException("Member X25519 public keys must use exactly 32 raw RFC 7748 bytes.");
        }

        return VaultKeyFingerprint.Compute(rawPublicKey, VaultKeyKind.MemberX25519);
    }
}

internal enum VaultKeyKind : ushort
{
    AgentX25519 = 1,
    AgentEd25519 = 2,
    VaultSigningEd25519 = 3,
    VaultMessageX25519 = 4,
    MemberX25519 = 5,
}

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
