using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Palladin.Module.Identity.Infrastructure.Recovery;

// Seals the recovery challenge with libsodium crypto_box_seal, the primitive clients already open for
// X25519 wrappers. NSec bundles libsodium but does not expose sealed boxes, hence the direct import.
// The distinct magic and user binding stop the challenge being confused with any other sealed package
// for the same identity key, so a client never returns the plaintext of a Vault key wrapper as proof.
internal static class RecoveryChallengeSealer
{
    internal const int SecretBytes = 32;
    private const int SealBytes = 48;
    private static readonly byte[] Magic = "PLDNRCV1"u8.ToArray();

    static RecoveryChallengeSealer()
    {
        if (sodium_init() < 0)
        {
            throw new CryptographicException("libsodium could not be initialized.");
        }
    }

    internal static byte[] Seal(Guid userId, ReadOnlySpan<byte> secret, byte[] recipientPublicKey)
    {
        if (secret.Length != SecretBytes || recipientPublicKey.Length != 32)
        {
            throw new ArgumentException("Recovery challenge secret and recipient key must be 32 bytes.");
        }

        var package = new byte[Magic.Length + 16 + SecretBytes];
        try
        {
            Magic.CopyTo(package, 0);
            userId.TryWriteBytes(package.AsSpan(Magic.Length, 16), bigEndian: true, out _);
            secret.CopyTo(package.AsSpan(Magic.Length + 16));

            var sealedBox = new byte[package.Length + SealBytes];
            if (crypto_box_seal(sealedBox, package, (ulong)package.Length, recipientPublicKey) != 0)
            {
                throw new CryptographicException("Recovery challenge could not be sealed.");
            }

            return sealedBox;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(package);
        }
    }

    [DllImport("libsodium", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sodium_init();

    [DllImport("libsodium", CallingConvention = CallingConvention.Cdecl)]
    private static extern int crypto_box_seal(byte[] sealedBox, byte[] message, ulong messageLength, byte[] recipientPublicKey);
}
