using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;
using Palladin.Module.Agents.Domain;

namespace Palladin.Module.Agents.Infrastructure.Pairing;

internal sealed class AgentPairingCredentialProtector
{
    internal const string Suite = "palladin-agent-pairing-x25519-xchacha20poly1305-v1";
    private static readonly byte[] HkdfInfo = "palladin/agent-pairing/v1/credential"u8.ToArray();

    internal bool CanEstablishSharedSecret(string recipientPublicKeyBase64)
    {
        try
        {
            var recipient = PublicKey.Import(
                KeyAgreementAlgorithm.X25519,
                Convert.FromBase64String(recipientPublicKeyBase64),
                KeyBlobFormat.RawPublicKey);
            using var ephemeral = Key.Create(KeyAgreementAlgorithm.X25519);
            using var shared = KeyAgreementAlgorithm.X25519.Agree(
                ephemeral,
                recipient,
                new SharedSecretCreationParameters());
            return shared is not null;
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            return false;
        }
    }

    internal ApiKeyCredentialEnvelope Protect(
        Guid pairingId,
        Guid organizationId,
        Guid agentId,
        Guid apiKeyId,
        string recipientPublicKeyBase64,
        ReadOnlySpan<byte> plaintext)
    {
        var recipientBytes = Convert.FromBase64String(recipientPublicKeyBase64);
        var recipient = PublicKey.Import(
            KeyAgreementAlgorithm.X25519,
            recipientBytes,
            KeyBlobFormat.RawPublicKey);
        using var ephemeral = Key.Create(
            KeyAgreementAlgorithm.X25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        using var shared = KeyAgreementAlgorithm.X25519.Agree(
            ephemeral,
            recipient,
            new SharedSecretCreationParameters())
            ?? throw new CryptographicException("The pairing public key cannot establish a shared secret.");

        var salt = Encoding.UTF8.GetBytes(pairingId.ToString("D"));
        using var key = KeyDerivationAlgorithm.HkdfSha256.DeriveKey(
            shared,
            salt,
            HkdfInfo,
            AeadAlgorithm.XChaCha20Poly1305,
            new KeyCreationParameters());
        var nonce = RandomNumberGenerator.GetBytes(AeadAlgorithm.XChaCha20Poly1305.NonceSize);
        var aad = BuildAssociatedData(
            pairingId, organizationId, agentId, apiKeyId, recipientPublicKeyBase64);
        var ciphertext = AeadAlgorithm.XChaCha20Poly1305.Encrypt(key, nonce, aad, plaintext);
        return new ApiKeyCredentialEnvelope(
            Suite,
            Convert.ToBase64String(ephemeral.PublicKey.Export(KeyBlobFormat.RawPublicKey)),
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(ciphertext));
    }

    internal static byte[] BuildAssociatedData(
        Guid pairingId,
        Guid organizationId,
        Guid agentId,
        Guid apiKeyId,
        string recipientPublicKeyBase64) =>
        Encoding.UTF8.GetBytes(
            $"palladin-agent-pairing-v1\n{pairingId:D}\n{recipientPublicKeyBase64}\n{organizationId:D}\n{agentId:D}\n{apiKeyId:D}");
}
