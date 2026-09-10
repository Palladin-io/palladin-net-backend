using System.Text.Json;
using NodaTime;
using Palladin.Module.Identity.Infrastructure.SharedUnlock;
using Shouldly;

namespace Palladin.Tests.Unit.Modules.Identity;

public sealed class SharedUnlockIdentityProofTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void When_NodeSignsAuthorizedContext_Then_NSecAcceptsExactCanonicalBytes(int index)
    {
        // Given
        using var fixture = ReadFixture();
        var vector = fixture.RootElement.GetProperty("positive")[index];
        var authority = ReadAuthority(vector);
        var purpose = ReadPurpose(vector);

        // When
        var encoded = SharedUnlockIdentityProof.Encode(authority, purpose);
        var valid = SharedUnlockIdentityProof.Verify(authority, purpose,
            Decode(vector.GetProperty("signature").GetString()!), authority.IssuedAt);

        // Then
        encoded.ShouldBe(Decode(vector.GetProperty("message").GetString()!));
        valid.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void When_AuthorityOrPurposeIsSubstituted_Then_OriginalSignatureIsRejected(int index)
    {
        // Given
        using var fixture = ReadFixture();
        var vector = fixture.RootElement.GetProperty("positive")[index];
        var authority = ReadAuthority(vector);
        var purpose = ReadPurpose(vector);
        var signature = Decode(vector.GetProperty("signature").GetString()!);
        SharedUnlockProofAuthority[] altered =
        [
            authority with { OperationId = Guid.Parse("99999999-9999-4999-8999-999999999999") },
            authority with { Challenge = Changed(authority.Challenge) },
            authority with { TranscriptHash = Changed(authority.TranscriptHash) },
            authority with { RecipientProofPublicKey = Changed(authority.RecipientProofPublicKey) },
            authority with { IssuedAt = authority.IssuedAt + Duration.FromMilliseconds(1) },
            authority with { ExpiresAt = authority.ExpiresAt - Duration.FromMilliseconds(1) },
        ];

        // When
        var results = altered.Select(candidate => SharedUnlockIdentityProof.Verify(
            candidate, purpose, signature, authority.IssuedAt + Duration.FromSeconds(1))).ToArray();
        var reflected = SharedUnlockIdentityProof.Verify(authority,
            purpose == SharedUnlockProofPurpose.Consume ? SharedUnlockProofPurpose.Commit : SharedUnlockProofPurpose.Consume,
            signature, authority.IssuedAt);

        // Then
        results.ShouldAllBe(result => !result);
        reflected.ShouldBeFalse();
    }

    [Fact]
    public void When_ProofIsMalformedOrOutsideLifetime_Then_VerificationFailsClosed()
    {
        // Given
        using var fixture = ReadFixture();
        var vector = fixture.RootElement.GetProperty("positive")[0];
        var authority = ReadAuthority(vector);
        var signature = Decode(vector.GetProperty("signature").GetString()!);

        // When
        var expired = SharedUnlockIdentityProof.Verify(authority, SharedUnlockProofPurpose.Consume, signature, authority.ExpiresAt);
        var future = SharedUnlockIdentityProof.Verify(authority, SharedUnlockProofPurpose.Consume, signature, authority.IssuedAt - Duration.FromMilliseconds(1));
        var corrupted = SharedUnlockIdentityProof.Verify(authority, SharedUnlockProofPurpose.Consume, Changed(signature), authority.IssuedAt);
        var shortSignature = SharedUnlockIdentityProof.Verify(authority, SharedUnlockProofPurpose.Consume, signature[..63], authority.IssuedAt);
        var longSignature = SharedUnlockIdentityProof.Verify(authority, SharedUnlockProofPurpose.Consume, [..signature, 0], authority.IssuedAt);
        var unknownPurpose = SharedUnlockIdentityProof.Verify(authority, (SharedUnlockProofPurpose)3, signature, authority.IssuedAt);
        var excessiveLifetime = SharedUnlockIdentityProof.Verify(authority with { ExpiresAt = authority.IssuedAt + Duration.FromSeconds(31) }, SharedUnlockProofPurpose.Consume, signature, authority.IssuedAt);
        var malformedKey = SharedUnlockIdentityProof.Verify(authority with { RecipientProofPublicKey = [] }, SharedUnlockProofPurpose.Consume, signature, authority.IssuedAt);
        var fractionalTimestamp = SharedUnlockIdentityProof.Verify(authority with { ExpiresAt = authority.ExpiresAt - Duration.FromNanoseconds(1) }, SharedUnlockProofPurpose.Consume, signature, authority.IssuedAt);

        // Then
        new[] { expired, future, corrupted, shortSignature, longSignature, unknownPurpose, excessiveLifetime, malformedKey, fractionalTimestamp }
            .ShouldAllBe(result => !result);
    }

    private static JsonDocument ReadFixture() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "SharedUnlock", "identity-proof-v1.json")));

    private static SharedUnlockProofAuthority ReadAuthority(JsonElement vector)
    {
        var context = vector.GetProperty("context");
        return new SharedUnlockProofAuthority(
            context.GetProperty("operationId").GetGuid(),
            Decode(context.GetProperty("challenge").GetString()!),
            Decode(context.GetProperty("transcriptHash").GetString()!),
            Decode(vector.GetProperty("recipientProofPublicKey").GetString()!),
            Instant.FromUnixTimeMilliseconds(context.GetProperty("issuedAtMs").GetInt64()),
            Instant.FromUnixTimeMilliseconds(context.GetProperty("expiresAtMs").GetInt64()));
    }

    private static SharedUnlockProofPurpose ReadPurpose(JsonElement vector) =>
        vector.GetProperty("purpose").GetString() == "consume" ? SharedUnlockProofPurpose.Consume : SharedUnlockProofPurpose.Commit;

    private static byte[] Decode(string value) => Convert.FromBase64String(
        value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));

    private static byte[] Changed(byte[] value)
    {
        var copy = value.ToArray();
        copy[0] ^= 1;
        return copy;
    }
}
