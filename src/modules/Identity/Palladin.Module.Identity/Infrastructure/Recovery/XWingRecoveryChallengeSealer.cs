using System.Security.Cryptography;
using NSec.Cryptography;
using Palladin.Core.Cryptography;

namespace Palladin.Module.Identity.Infrastructure.Recovery;

// identity-kdf v3 recoveryChallenge (version 2). The PLDNRCV2 domain in the KDF and AAD separates the challenge
// from every key-wrapper package sealed to the same Member key, so it cannot act as a decryption oracle.
internal static class XWingRecoveryChallengeSealer
{
    internal const ushort Version = 2;
    private static readonly byte[] Label = "PLDNRCV2"u8.ToArray();

    internal static byte[] Seal(Guid userId, ReadOnlySpan<byte> secret, byte[] memberPublicKey)
    {
        var (ciphertext, sharedSecret) = XWingKem.Encapsulate(memberPublicKey);
        return Seal(userId, secret, memberPublicKey, ciphertext, sharedSecret);
    }

    internal static byte[] Seal(
        Guid userId,
        ReadOnlySpan<byte> secret,
        byte[] memberPublicKey,
        byte[] kemCiphertext,
        byte[] sharedSecret)
    {
        if (secret.Length != RecoveryChallengeSealer.SecretBytes)
        {
            throw new ArgumentException("Recovery challenge secrets must contain 32 bytes.");
        }

        Span<byte> userIdBytes = stackalloc byte[16];
        userId.TryWriteBytes(userIdBytes, bigEndian: true, out _);
        byte[] info = [.. Label, .. userIdBytes, .. SHA256.HashData(memberPublicKey), .. SHA256.HashData(kemCiphertext)];
        byte[] aad = [.. Label, .. userIdBytes];
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, [], info);
        try
        {
            using var aeadKey = Key.Import(AeadAlgorithm.XChaCha20Poly1305, key, KeyBlobFormat.RawSymmetricKey);
            var sealedSecret = AeadAlgorithm.XChaCha20Poly1305.Encrypt(aeadKey, new byte[24], aad, secret);
            return [.. kemCiphertext, .. sealedSecret];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(sharedSecret);
        }
    }
}
