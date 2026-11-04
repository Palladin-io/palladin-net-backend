using System.Security.Cryptography;
using JetBrains.Annotations;
using NSec.Cryptography;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Kems;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace Palladin.Core.Cryptography;

// X-Wing per draft-connolly-cfrg-xwing-kem-10: managed ML-KEM-768 from BouncyCastle (System.Security.Cryptography.MLKem
// is unavailable on macOS and on OpenSSL 3.0 Linux images), X25519 from NSec/libsodium and the SHA3-256 combiner.
[PublicAPI]
public static class XWingKem
{
    public const int PublicKeyBytes = MlKemPublicKeyBytes + X25519Bytes;
    public const int CiphertextBytes = MlKemCiphertextBytes + X25519Bytes;
    public const int SharedSecretBytes = 32;

    private const int MlKemPublicKeyBytes = 1184;
    private const int MlKemCiphertextBytes = 1088;
    private const int X25519Bytes = 32;
    private const int EncapsulationRandomnessBytes = 64;
    private static readonly byte[] Label = [0x5c, 0x2e, 0x2f, 0x2f, 0x5e, 0x5c];

    public static (byte[] Ciphertext, byte[] SharedSecret) Encapsulate(ReadOnlySpan<byte> publicKey)
    {
        var randomness = RandomNumberGenerator.GetBytes(EncapsulationRandomnessBytes);
        try
        {
            return Encapsulate(publicKey, randomness);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(randomness);
        }
    }

    internal static (byte[] Ciphertext, byte[] SharedSecret) Encapsulate(
        ReadOnlySpan<byte> publicKey,
        ReadOnlySpan<byte> randomness)
    {
        if (publicKey.Length != PublicKeyBytes || randomness.Length != EncapsulationRandomnessBytes)
        {
            throw new CryptographicException("X-Wing encapsulation requires a 1216-byte key and 64 bytes of randomness.");
        }

        var mlKemPublicKey = MLKemPublicKeyParameters.FromEncoding(
            MLKemParameters.ml_kem_768, publicKey[..MlKemPublicKeyBytes].ToArray());
        var x25519PublicKey = publicKey[MlKemPublicKeyBytes..].ToArray();

        var mlKemRandomness = randomness[..32].ToArray();
        var encapsulator = new MLKemEncapsulator(MLKemParameters.ml_kem_768);
        encapsulator.Init(new ParametersWithRandom(mlKemPublicKey, new FixedSecureRandom(mlKemRandomness)));
        var ciphertext = new byte[CiphertextBytes];
        var mlKemSecret = new byte[SharedSecretBytes];
        var ephemeral = randomness[32..].ToArray();
        byte[]? x25519Secret = null;
        try
        {
            encapsulator.Encapsulate(ciphertext, 0, MlKemCiphertextBytes, mlKemSecret, 0, SharedSecretBytes);
            var ephemeralPublicKey = X25519PublicKey(ephemeral);
            ephemeralPublicKey.CopyTo(ciphertext, MlKemCiphertextBytes);
            x25519Secret = X25519(ephemeral, x25519PublicKey);
            return (ciphertext, Combine(mlKemSecret, x25519Secret, ephemeralPublicKey, x25519PublicKey));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(mlKemRandomness);
            CryptographicOperations.ZeroMemory(mlKemSecret);
            CryptographicOperations.ZeroMemory(ephemeral);
            if (x25519Secret is not null)
            {
                CryptographicOperations.ZeroMemory(x25519Secret);
            }
        }
    }

    internal static byte[] PublicKeyFromSeed(ReadOnlySpan<byte> seed)
    {
        var expanded = Expand(seed);
        try
        {
            var mlKem = MLKemPrivateKeyParameters.FromSeed(MLKemParameters.ml_kem_768, expanded[..64]);
            return [.. mlKem.GetPublicKeyEncoded(), .. X25519PublicKey(expanded[64..])];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expanded);
        }
    }

    internal static byte[] Decapsulate(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> ciphertext)
    {
        if (ciphertext.Length != CiphertextBytes)
        {
            throw new CryptographicException("X-Wing ciphertext must contain exactly 1120 bytes.");
        }

        var expanded = Expand(seed);
        var mlKemSecret = new byte[SharedSecretBytes];
        byte[]? x25519Secret = null;
        try
        {
            var mlKem = MLKemPrivateKeyParameters.FromSeed(MLKemParameters.ml_kem_768, expanded[..64]);
            var decapsulator = new MLKemDecapsulator(MLKemParameters.ml_kem_768);
            decapsulator.Init(mlKem);
            decapsulator.Decapsulate(ciphertext[..MlKemCiphertextBytes].ToArray(), 0, MlKemCiphertextBytes,
                mlKemSecret, 0, SharedSecretBytes);
            var x25519Private = expanded[64..];
            var ephemeralPublicKey = ciphertext[MlKemCiphertextBytes..].ToArray();
            x25519Secret = X25519(x25519Private, ephemeralPublicKey);
            return Combine(mlKemSecret, x25519Secret, ephemeralPublicKey, X25519PublicKey(x25519Private));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expanded);
            CryptographicOperations.ZeroMemory(mlKemSecret);
            if (x25519Secret is not null)
            {
                CryptographicOperations.ZeroMemory(x25519Secret);
            }
        }
    }

    private static byte[] Expand(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != 32)
        {
            throw new CryptographicException("X-Wing private seeds must contain exactly 32 bytes.");
        }

        var shake = new ShakeDigest(256);
        shake.BlockUpdate(seed);
        var expanded = new byte[96];
        shake.OutputFinal(expanded, 0, expanded.Length);
        return expanded;
    }

    private static byte[] Combine(byte[] mlKemSecret, byte[] x25519Secret, byte[] ciphertextX25519, byte[] publicKeyX25519)
    {
        var digest = new Sha3Digest(256);
        foreach (var part in new[] { mlKemSecret, x25519Secret, ciphertextX25519, publicKeyX25519, Label })
        {
            digest.BlockUpdate(part, 0, part.Length);
        }

        var sharedSecret = new byte[SharedSecretBytes];
        digest.DoFinal(sharedSecret, 0);
        return sharedSecret;
    }

    private static byte[] X25519PublicKey(byte[] privateKey)
    {
        using var key = Key.Import(KeyAgreementAlgorithm.X25519, privateKey, KeyBlobFormat.RawPrivateKey);
        return key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
    }

    private static byte[] X25519(byte[] privateKey, byte[] publicKey)
    {
        using var key = Key.Import(KeyAgreementAlgorithm.X25519, privateKey, KeyBlobFormat.RawPrivateKey);
        var peer = PublicKey.Import(KeyAgreementAlgorithm.X25519, publicKey, KeyBlobFormat.RawPublicKey);
        using var secret = KeyAgreementAlgorithm.X25519.Agree(key, peer,
                               new SharedSecretCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport })
                           ?? throw new CryptographicException("X25519 produced an all-zero shared secret.");
        return secret.Export(SharedSecretBlobFormat.RawSharedSecret);
    }

    private sealed class FixedSecureRandom(byte[] value) : SecureRandom
    {
        private int _offset;

        public override void NextBytes(byte[] buf) => NextBytes(buf, 0, buf.Length);

        public override void NextBytes(byte[] buf, int off, int len) => NextBytes(buf.AsSpan(off, len));

        public override void NextBytes(Span<byte> buffer)
        {
            if (_offset + buffer.Length > value.Length)
            {
                throw new CryptographicException("X-Wing encapsulation randomness was exhausted.");
            }

            value.AsSpan(_offset, buffer.Length).CopyTo(buffer);
            _offset += buffer.Length;
        }
    }
}
