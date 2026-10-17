using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using NSubstitute;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Vault.Infrastructure.Sharing;

namespace Palladin.Tests.Unit.Modules.Vault;

public sealed class EntryShareSecurityTests
{
    [Fact]
    public void When_AnOtpIsPersistedForDelivery_Then_OnlyItsSessionAndGenerationCanOpenIt()
    {
        // Given
        var security = CreateSecurity();
        var share = Guid.NewGuid();
        var session = Guid.NewGuid();
        const string code = "493827";

        // When
        var protectedCode = security.ProtectOtp(share, session, 1, code);

        // Then
        protectedCode.ShouldNotContain(code);
        security.UnprotectOtp(share, session, 1, protectedCode).ShouldBe(code);
        Should.Throw<CryptographicException>(() => security.UnprotectOtp(Guid.NewGuid(), session, 1, protectedCode));
        Should.Throw<CryptographicException>(() => security.UnprotectOtp(share, Guid.NewGuid(), 1, protectedCode));
        Should.Throw<CryptographicException>(() => security.UnprotectOtp(share, session, 2, protectedCode));
        var modified = WebEncoders.Base64UrlDecode(protectedCode);
        modified[^1] ^= 1;
        Should.Throw<CryptographicException>(() => security.UnprotectOtp(share, session, 1, WebEncoders.Base64UrlEncode(modified)));
    }

    [Theory]
    [InlineData(EntryShareProtection.Pin, "123456")]
    [InlineData(EntryShareProtection.Password, "a strong test passphrase")]
    public void When_ASecretIsHashed_Then_TheVerifierIsSaltedAndBoundToItsShare(
        EntryShareProtection protection, string secret)
    {
        // Given
        var security = CreateSecurity();
        var shareId = Guid.NewGuid();

        // When
        var first = security.CreateSecretVerifier(shareId, protection, secret);
        var second = security.CreateSecretVerifier(shareId, protection, secret);

        // Then
        first.ShouldNotBe(second);
        first.ShouldNotBeNull().ShouldNotContain(secret);
        security.VerifySecret(shareId, protection, secret, first).ShouldBeTrue();
        security.VerifySecret(Guid.NewGuid(), protection, secret, first).ShouldBeFalse();
        security.VerifySecret(shareId, protection, secret + "7", first).ShouldBeFalse();
        Convert.FromBase64String(first!)[0].ShouldBe((byte)1);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("12345a")]
    [InlineData("１２３４５６")]
    [InlineData(" 123456")]
    public void When_PinIsTooShortOrNotAsciiDigits_Then_ItCannotBeEnabled(string pin)
    {
        // Given
        var security = CreateSecurity();

        // When
        Action action = () => security.CreateSecretVerifier(Guid.NewGuid(), EntryShareProtection.Pin, pin);

        // Then
        action.ShouldThrow<DomainException>();
    }

    [Fact]
    public void When_NoExtraProtectionIsSelected_Then_NoSecretVerifierIsStored()
    {
        // Given
        var security = CreateSecurity();

        // When
        var verifier = security.CreateSecretVerifier(Guid.NewGuid(), EntryShareProtection.None, null);

        // Then
        verifier.ShouldBeNull();
        Should.Throw<DomainException>(() =>
            security.CreateSecretVerifier(Guid.NewGuid(), EntryShareProtection.None, "unwanted secret"));
    }

    [Fact]
    public void When_AccessOrSessionVerifierIsReplayedInAnotherScope_Then_VerificationFails()
    {
        // Given
        var security = CreateSecurity();
        var id = Guid.NewGuid();
        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var hash = security.HashAccessToken(id, token);

        // When
        var sameScope = security.VerifyAccessToken(id, token, hash);
        var foreignScope = security.VerifyAccessToken(Guid.NewGuid(), token, hash);
        var wrongPurpose = security.VerifySessionToken(id, token, hash);

        // Then
        sameScope.ShouldBeTrue();
        foreignScope.ShouldBeFalse();
        wrongPurpose.ShouldBeFalse();
        security.VerifyAccessToken(id, token + "=", hash).ShouldBeFalse();
        security.VerifyAccessToken(id, new string('!', 43), hash).ShouldBeFalse();
    }

    [Fact]
    public void When_VerifyingOtp_Then_OnlyTheExactSessionAndCodeMatch()
    {
        // Given
        var security = CreateSecurity();
        var sessionId = Guid.NewGuid();
        var otp = security.GenerateOtp();
        var verifier = security.HashOtp(sessionId, otp);

        // When
        var correct = security.VerifyOtp(sessionId, otp, verifier);

        // Then
        otp.Length.ShouldBe(6);
        otp.All(char.IsAsciiDigit).ShouldBeTrue();
        correct.ShouldBeTrue();
        security.VerifyOtp(Guid.NewGuid(), otp, verifier).ShouldBeFalse();
        security.VerifyOtp(sessionId, "12345", verifier).ShouldBeFalse();
        security.VerifyOtp(sessionId, "abcdef", verifier).ShouldBeFalse();
        security.VerifyOtp(sessionId, otp, null).ShouldBeFalse();
    }

    [Fact]
    public void When_RecipientEmailIsProtected_Then_ItCannotBeMovedToAnotherShare()
    {
        // Given
        var security = CreateSecurity();
        var shareId = Guid.NewGuid();
        const string address = "recipient@example.invalid";

        // When
        var protectedEmail = security.ProtectRecipientEmail(shareId, address);

        // Then
        protectedEmail.ShouldNotContain(address);
        security.UnprotectRecipientEmail(shareId, protectedEmail).ShouldBe(address);
        Should.Throw<CryptographicException>(() =>
            security.UnprotectRecipientEmail(Guid.NewGuid(), protectedEmail));
        var tampered = WebEncoders.Base64UrlDecode(protectedEmail);
        tampered[^1] ^= 1;
        Should.Throw<CryptographicException>(() =>
            security.UnprotectRecipientEmail(shareId, WebEncoders.Base64UrlEncode(tampered)));
    }

    [Fact]
    public void When_AStoredVerifierIsMalformed_Then_ItFailsClosed()
    {
        // Given
        var security = CreateSecurity();

        // When
        var verified = security.VerifySecret(Guid.NewGuid(), EntryShareProtection.Pin, "123456", "not-base64");

        // Then
        verified.ShouldBeFalse();
    }

    [Fact]
    public void When_UnsafeOptionsAreSupplied_Then_InitializationFailsClosed()
    {
        // Given
        var keyDeriver = Substitute.For<IServerKeyDeriver>();
        var invalid = Options.Create(new EntrySharingOptions { TotalFailedAttemptLimit = 0 });

        // When
        Action action = () => new EntryShareSecurity(keyDeriver, invalid);

        // Then
        action.ShouldThrow<InvalidOperationException>();
    }

    private static EntryShareSecurity CreateSecurity()
    {
        var deriver = Substitute.For<IServerKeyDeriver>();
        deriver.DeriveHmacKey(Arg.Any<string>())
            .Returns(call => HMACSHA256.HashData(new byte[32], Encoding.UTF8.GetBytes(call.Arg<string>())));
        return new EntryShareSecurity(deriver,
            Options.Create(new EntrySharingOptions { PasswordHashIterations = 100_000 }));
    }
}
