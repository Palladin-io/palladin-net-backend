using System.Buffers.Binary;
using System.Text;
using NSec.Cryptography;
using Palladin.Core.Cryptography;

namespace Palladin.Module.Agents.Infrastructure.Crypto;

internal static class AgentEncryptionKeys
{
    private const ushort AgentEncryptionKeyKind = 1;
    private const ushort ProtocolVersion = 2;
    private static readonly byte[] RotationDomain = Encoding.ASCII.GetBytes("palladin.agent-encryption-key-rotation.v1");

    // Enrollment and pairing still register the 32-byte X25519 identity key; it is also the first encryption key.
    internal static KeyWrapperSuite EnrollmentSuite { get; } = KeyWrapperSuites.Resolve("palladin-x25519-sealed-box-v1");

    internal static byte[] Fingerprint(string suiteId, byte[] publicKey) =>
        KeyWrapperSuites.ResolveForOpen(suiteId).Fingerprint(AgentEncryptionKeyKind, publicKey);

    // palladin-agent runtime identity.rs: domain || 0x00 || u16be(2) || agentId[16] || previous[32]
    // || u16be(len) || nextSuiteId || next[32], signed with the Agent's registered Ed25519 key.
    internal static bool VerifyRotation(
        byte[] signingPublicKey,
        Guid agentId,
        byte[] previousFingerprint,
        string nextSuiteId,
        byte[] nextFingerprint,
        byte[] signature)
    {
        if (signingPublicKey.Length != 32 || signature.Length != 64)
        {
            return false;
        }

        var suite = Encoding.ASCII.GetBytes(nextSuiteId);
        var statement = new byte[RotationDomain.Length + 1 + 2 + 16 + 32 + 2 + suite.Length + 32];
        var span = statement.AsSpan();
        RotationDomain.CopyTo(span);
        var offset = RotationDomain.Length + 1;
        BinaryPrimitives.WriteUInt16BigEndian(span[offset..], ProtocolVersion);
        offset += 2;
        agentId.TryWriteBytes(span.Slice(offset, 16), bigEndian: true, out _);
        offset += 16;
        previousFingerprint.CopyTo(span[offset..]);
        offset += 32;
        BinaryPrimitives.WriteUInt16BigEndian(span[offset..], checked((ushort)suite.Length));
        offset += 2;
        suite.CopyTo(span[offset..]);
        offset += suite.Length;
        nextFingerprint.CopyTo(span[offset..]);

        var key = PublicKey.Import(SignatureAlgorithm.Ed25519, signingPublicKey, KeyBlobFormat.RawPublicKey);
        return SignatureAlgorithm.Ed25519.Verify(key, statement, signature);
    }
}
