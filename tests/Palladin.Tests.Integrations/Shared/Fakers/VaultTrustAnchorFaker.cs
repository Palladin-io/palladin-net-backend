using System.Security.Cryptography;
using NSec.Cryptography;

namespace Palladin.Tests.Integrations.Shared.Fakers;

internal static class VaultTrustAnchorFaker
{
    private static readonly byte[] SigningPrivateKey = CreateSigningPrivateKey();
    private static readonly byte[] RotatedSigningPrivateKey = CreateSigningPrivateKey();

    internal static byte[] AgentMessagePublicKey { get; } = RandomNumberGenerator.GetBytes(32);
    internal static byte[] RotatedAgentMessagePublicKey { get; } = RandomNumberGenerator.GetBytes(32);

    internal static byte[] ManifestSigningPublicKey
    {
        get
        {
            using var key = CreateManifestSigningKey();
            return key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        }
    }

    internal static Key CreateManifestSigningKey() => Key.Import(
        SignatureAlgorithm.Ed25519,
        SigningPrivateKey,
        KeyBlobFormat.RawPrivateKey,
        new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

    internal static byte[] SignWithManifestKey(byte[] input, bool rotated = false)
    {
        using var key = rotated
            ? Key.Import(SignatureAlgorithm.Ed25519, RotatedSigningPrivateKey, KeyBlobFormat.RawPrivateKey)
            : CreateManifestSigningKey();
        return SignatureAlgorithm.Ed25519.Sign(key, input);
    }

    internal static byte[] RotatedManifestSigningPublicKey
    {
        get
        {
            using var key = Key.Import(SignatureAlgorithm.Ed25519, RotatedSigningPrivateKey,
                KeyBlobFormat.RawPrivateKey);
            return key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        }
    }

    private static byte[] CreateSigningPrivateKey()
    {
        using var key = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        });
        return key.Export(KeyBlobFormat.RawPrivateKey);
    }
}
