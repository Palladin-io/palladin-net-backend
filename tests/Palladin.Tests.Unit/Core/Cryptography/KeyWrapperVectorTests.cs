using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Palladin.Core.Cryptography;
using Palladin.Core.Types.Exceptions;

namespace Palladin.Tests.Unit.Core.Cryptography;

public sealed class KeyWrapperVectorTests
{
    private static readonly JsonElement Vectors = JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "KeyWrapper", "v1", "vectors.json"))).RootElement;

    [Fact]
    public void When_DraftTenKnownAnswerIsReplayed_Then_XWingMatchesPublicKeyCiphertextAndSecret()
    {
        // Given
        var kat = Vectors.GetProperty("xwingKnownAnswer");
        var seed = Convert.FromHexString(kat.GetProperty("seedHex").GetString()!);
        var randomness = Convert.FromHexString(kat.GetProperty("encapsulationRandomnessHex").GetString()!);

        // When
        var publicKey = XWingKem.PublicKeyFromSeed(seed);
        var (ciphertext, sharedSecret) = XWingKem.Encapsulate(publicKey, randomness);

        // Then
        Convert.ToHexStringLower(SHA256.HashData(publicKey)).ShouldBe(kat.GetProperty("publicKeySha256Hex").GetString());
        Convert.ToHexStringLower(SHA256.HashData(ciphertext)).ShouldBe(kat.GetProperty("ciphertextSha256Hex").GetString());
        Convert.ToHexStringLower(sharedSecret).ShouldBe(kat.GetProperty("sharedSecretHex").GetString());
        XWingKem.Decapsulate(seed, ciphertext).ShouldBe(sharedSecret);
    }

    [Fact]
    public void When_PositiveVectorsAreChecked_Then_EverySuiteReproducesTheDirectoryFingerprintAndPackageLength()
    {
        // Given
        var vectors = Vectors.GetProperty("positive").EnumerateArray()
            .Concat(Vectors.GetProperty("legacy").EnumerateArray())
            .ToList();

        // When / Then
        vectors.ShouldNotBeEmpty();
        foreach (var vector in vectors)
        {
            var descriptor = vector.GetProperty("descriptor");
            var recipient = vector.GetProperty("recipient");
            var suite = KeyWrapperSuites.Resolve(recipient.GetProperty("directorySuiteId").GetString());
            var publicKey = Convert.FromHexString(recipient.GetProperty("publicKeyHex").GetString()!);
            var package = WebEncoders.Base64UrlDecode(vector.GetProperty("encodedSealedKeyPackage").GetString()!);

            WebEncoders.Base64UrlEncode(suite.Fingerprint(recipient.GetProperty("keyKind").GetUInt16(), publicKey))
                .ShouldBe(descriptor.GetProperty("recipientFingerprint").GetString(), vector.GetProperty("name").GetString());
            Should.NotThrow(() => suite.ValidatePackage(package));
        }
    }

    [Theory]
    [InlineData("package-one-byte-short")]
    [InlineData("package-one-byte-long")]
    [InlineData("legacy-package-under-new-suite")]
    [InlineData("new-suite-package-under-legacy-suite")]
    [InlineData("legacy-package-one-byte-short")]
    public void When_PackageLengthViolatesTheDescriptorSuite_Then_ValidationFailsClosed(string name)
    {
        // Given
        var vector = Negative(name);
        var suite = KeyWrapperSuites.Resolve(vector.GetProperty("descriptor").GetProperty("wrapperSuiteId").GetString());

        // When / Then
        Should.Throw<DomainException>(() =>
            suite.ValidatePackage(WebEncoders.Base64UrlDecode(vector.GetProperty("encodedSealedKeyPackage").GetString()!)));
    }

    [Theory]
    [InlineData("unknown-suite-id")]
    [InlineData("suite-id-case-variant")]
    public void When_SuiteIsNotRegistered_Then_ResolutionFailsClosed(string name) =>
        Should.Throw<DomainException>(() =>
            KeyWrapperSuites.Resolve(Negative(name).GetProperty("descriptor").GetProperty("wrapperSuiteId").GetString()));

    [Fact]
    public void When_ReadOnlySuiteIsUsedForSealing_Then_ItIsRejectedButStillOpenable()
    {
        // Given
        var suiteId = Negative("read-only-suite-cannot-seal").GetProperty("descriptor").GetProperty("wrapperSuiteId").GetString();

        // When / Then
        Should.Throw<DomainException>(() => KeyWrapperSuites.ResolveForSeal(suiteId));
        KeyWrapperSuites.ResolveForOpen(suiteId).Id.ShouldBe(suiteId);
        KeyWrapperSuites.ResolveForSeal(KeyWrapperSuites.Current.Id).ShouldBe(KeyWrapperSuites.Current);
    }

    private static JsonElement Negative(string name) =>
        Vectors.GetProperty("negative").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);
}
