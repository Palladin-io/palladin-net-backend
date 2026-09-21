using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Palladin.Module.Vault.Infrastructure.Sharing;

internal static class EntryShareActivityIdentity
{
    internal static Guid For(Guid shareId, long sequence)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);
        ReadOnlySpan<byte> domain = "PLDN-ENTRY-SHARE-ACTIVITY-v1"u8;
        Span<byte> input = stackalloc byte[domain.Length + 24];
        domain.CopyTo(input);
        shareId.TryWriteBytes(input[domain.Length..], bigEndian: true, out _);
        BinaryPrimitives.WriteInt64BigEndian(input[(domain.Length + 16)..], sequence);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(input, digest);
        digest[6] = (byte)((digest[6] & 0x0f) | 0x80);
        digest[8] = (byte)((digest[8] & 0x3f) | 0x80);
        return new Guid(digest[..16], bigEndian: true);
    }
}
