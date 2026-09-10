using System.Buffers.Binary;
using System.Text;

namespace Palladin.Module.Identity.Infrastructure.SharedUnlock;

internal static class SharedUnlockEncoding
{
    internal static void Write(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)bytes.Length));
        stream.Write(length);
        stream.Write(bytes);
    }
}
