using System.Buffers.Binary;
using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NodaTime;
using NSec.Cryptography;

namespace Palladin.Module.Identity.Infrastructure.SharedUnlock;

internal enum SharedUnlockProofPurpose
{
    Consume = 1,
    Commit = 2,
}

internal sealed record SharedUnlockProofAuthority(
    Guid OperationId,
    byte[] Challenge,
    byte[] TranscriptHash,
    byte[] RecipientProofPublicKey,
    Instant IssuedAt,
    Instant ExpiresAt);

internal static class SharedUnlockIdentityProof
{
    internal const string Protocol = "palladin.shared-unlock.identity-proof.v1";
    private static readonly Duration MaximumLifetime = Duration.FromSeconds(30);

    internal static bool Verify(
        SharedUnlockProofAuthority authority,
        SharedUnlockProofPurpose purpose,
        ReadOnlySpan<byte> signature,
        Instant now)
    {
        if (authority.OperationId == Guid.Empty
            || authority.Challenge is not { Length: 32 }
            || authority.TranscriptHash is not { Length: 32 }
            || authority.RecipientProofPublicKey is not { Length: 32 }
            || signature.Length != 64
            || purpose is not (SharedUnlockProofPurpose.Consume or SharedUnlockProofPurpose.Commit)
            || authority.IssuedAt.ToUnixTimeMilliseconds() <= 0
            || authority.IssuedAt != Instant.FromUnixTimeMilliseconds(authority.IssuedAt.ToUnixTimeMilliseconds())
            || authority.ExpiresAt != Instant.FromUnixTimeMilliseconds(authority.ExpiresAt.ToUnixTimeMilliseconds())
            || authority.ExpiresAt <= authority.IssuedAt
            || authority.ExpiresAt - authority.IssuedAt > MaximumLifetime
            || now < authority.IssuedAt
            || now >= authority.ExpiresAt)
        {
            return false;
        }

        try
        {
            var publicKey = PublicKey.Import(SignatureAlgorithm.Ed25519,
                authority.RecipientProofPublicKey, KeyBlobFormat.RawPublicKey);
            return SignatureAlgorithm.Ed25519.Verify(publicKey, Encode(authority, purpose), signature);
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException)
        {
            return false;
        }
    }

    internal static byte[] Encode(SharedUnlockProofAuthority authority, SharedUnlockProofPurpose purpose)
    {
        using var stream = new MemoryStream();
        Write(stream, Protocol);
        Write(stream, purpose switch
        {
            SharedUnlockProofPurpose.Consume => "consume",
            SharedUnlockProofPurpose.Commit => "commit",
            _ => throw new ArgumentOutOfRangeException(nameof(purpose)),
        });
        Write(stream, authority.OperationId.ToString("D"));
        Write(stream, Base64Url.EncodeToString(authority.Challenge));
        Write(stream, Base64Url.EncodeToString(authority.TranscriptHash));
        Write(stream, Base64Url.EncodeToString(authority.RecipientProofPublicKey));
        Write(stream, authority.IssuedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        Write(stream, authority.ExpiresAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        return stream.ToArray();
    }

    private static void Write(Stream stream, string value)
    {
        var encoded = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)encoded.Length));
        stream.Write(length);
        stream.Write(encoded);
    }
}
