using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using NSec.Cryptography;
using Palladin.Core.Cryptography;
using Palladin.Module.Identity.Infrastructure.Recovery;

namespace Palladin.Tests.Unit.Modules.Identity;

public sealed class RecoveryChallengeV2VectorTests
{
    private static readonly JsonElement Vector = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "IdentityKdf", "v3", "vectors.json")))
        .RootElement.GetProperty("recoveryChallenge");

    [Fact]
    public void When_ProtocolVectorIsSealedDeterministically_Then_PackageMatchesByteForByte()
    {
        // Given
        var seed = Convert.FromHexString(Vector.GetProperty("memberSeedHex").GetString()!);
        var publicKey = XWingKem.PublicKeyFromSeed(seed);
        var (ciphertext, sharedSecret) = XWingKem.Encapsulate(
            publicKey, Convert.FromHexString(Vector.GetProperty("encapsulationRandomnessHex").GetString()!));

        // When
        var package = XWingRecoveryChallengeSealer.Seal(
            Vector.GetProperty("userId").GetGuid(),
            WebEncoders.Base64UrlDecode(Vector.GetProperty("secretBase64url").GetString()!),
            publicKey,
            ciphertext,
            sharedSecret);

        // Then
        WebEncoders.Base64UrlEncode(SHA256.HashData(publicKey))
            .ShouldBe(Vector.GetProperty("memberPublicKeySha256Base64url").GetString());
        package.Length.ShouldBe(1168);
        WebEncoders.Base64UrlEncode(package).ShouldBe(Vector.GetProperty("package").GetString());
    }

    [Fact]
    public void When_PackageIsOpenedForAnotherUser_Then_AuthenticationFails()
    {
        // Given
        var seed = Convert.FromHexString(Vector.GetProperty("memberSeedHex").GetString()!);
        var package = WebEncoders.Base64UrlDecode(Vector.GetProperty("package").GetString()!);
        var otherUser = Vector.GetProperty("negative")[0].GetProperty("userId").GetGuid();
        var publicKey = XWingKem.PublicKeyFromSeed(seed);
        var kemCiphertext = package[..XWingKem.CiphertextBytes];
        var userIdBytes = otherUser.ToByteArray(bigEndian: true);
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, XWingKem.Decapsulate(seed, kemCiphertext), 32, [],
            [.. "PLDNRCV2"u8, .. userIdBytes, .. SHA256.HashData(publicKey), .. SHA256.HashData(kemCiphertext)]);
        using var aeadKey = Key.Import(AeadAlgorithm.XChaCha20Poly1305, key, KeyBlobFormat.RawSymmetricKey);

        // When
        var opened = AeadAlgorithm.XChaCha20Poly1305.Decrypt(
            aeadKey, new byte[24], (byte[])[.. "PLDNRCV2"u8, .. userIdBytes], package[XWingKem.CiphertextBytes..]);

        // Then
        opened.ShouldBeNull();
    }
}
