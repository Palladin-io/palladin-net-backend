using System.Security.Cryptography;
using NSec.Cryptography;
using Palladin.Core.Cryptography;

namespace Palladin.Tests.Integrations.Shared.Crypto;

internal sealed record XWingKeyPair(byte[] Seed, byte[] PublicKey);

// Test-side counterpart of the client X-Wing crypto module.
internal static class XWingTestKeys
{
    public static XWingKeyPair CreateKeyPair()
    {
        var seed = RandomNumberGenerator.GetBytes(32);
        return new XWingKeyPair(seed, XWingKem.PublicKeyFromSeed(seed));
    }

    public static byte[] OpenRecoveryChallenge(byte[] package, XWingKeyPair recipient, Guid userId)
    {
        var kemCiphertext = package[..XWingKem.CiphertextBytes];
        var userIdBytes = userId.ToByteArray(bigEndian: true);
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, XWingKem.Decapsulate(recipient.Seed, kemCiphertext), 32, [],
            [.. "PLDNRCV2"u8, .. userIdBytes, .. SHA256.HashData(recipient.PublicKey), .. SHA256.HashData(kemCiphertext)]);
        using var aeadKey = Key.Import(AeadAlgorithm.XChaCha20Poly1305, key, KeyBlobFormat.RawSymmetricKey);
        return AeadAlgorithm.XChaCha20Poly1305.Decrypt(aeadKey, new byte[24], (byte[])[.. "PLDNRCV2"u8, .. userIdBytes],
                   package[XWingKem.CiphertextBytes..])
               ?? throw new CryptographicException("Recovery challenge could not be opened.");
    }
}
