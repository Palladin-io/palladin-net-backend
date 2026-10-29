using System.Runtime.InteropServices;
using System.Security.Cryptography;
using NSec.Cryptography;

namespace Palladin.Tests.Integrations.Shared.Crypto;

internal sealed record X25519KeyPair(byte[] PublicKey, byte[] PrivateKey);

// Test-side counterpart of the client libsodium sealed-box open (crypto_box_seal_open).
internal static class X25519SealedBox
{
    private const int SealBytes = 48;

    public static X25519KeyPair CreateKeyPair()
    {
        using var key = Key.Create(
            KeyAgreementAlgorithm.X25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        return new X25519KeyPair(
            key.PublicKey.Export(KeyBlobFormat.RawPublicKey),
            key.Export(KeyBlobFormat.RawPrivateKey));
    }

    public static byte[] Open(byte[] sealedBox, X25519KeyPair recipient)
    {
        var message = new byte[sealedBox.Length - SealBytes];
        if (crypto_box_seal_open(message, sealedBox, (ulong)sealedBox.Length, recipient.PublicKey, recipient.PrivateKey) != 0)
        {
            throw new CryptographicException("Sealed box could not be opened.");
        }

        return message;
    }

    [DllImport("libsodium", CallingConvention = CallingConvention.Cdecl)]
    private static extern int crypto_box_seal_open(byte[] message, byte[] sealedBox, ulong sealedBoxLength, byte[] publicKey, byte[] privateKey);
}
