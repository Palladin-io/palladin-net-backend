using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Core.Types.Exceptions;

namespace Palladin.Module.Vault.Infrastructure.Sharing;

internal sealed class EntryShareSecurity
{
    private readonly byte[] accessKey;
    private readonly byte[] sessionKey;
    private readonly byte[] otpKey;
    private readonly byte[] secretPepper;
    private readonly byte[] emailKey;
    private readonly byte[] otpDeliveryKey;
    private readonly EntrySharingOptions options;
    private readonly PasswordHasher<object> secretHasher;
    private static readonly object HashingContext = new();

    public EntryShareSecurity(IServerKeyDeriver keyDeriver, IOptions<EntrySharingOptions> options)
    {
        this.options = options.Value;
        if (!this.options.IsValid())
        {
            throw new InvalidOperationException("Entry sharing configuration is invalid.");
        }

        accessKey = keyDeriver.DeriveHmacKey("PLDN-ENTRY-SHARE-ACCESS-v1");
        sessionKey = keyDeriver.DeriveHmacKey("PLDN-ENTRY-SHARE-SESSION-v1");
        otpKey = keyDeriver.DeriveHmacKey("PLDN-ENTRY-SHARE-OTP-v1");
        secretPepper = keyDeriver.DeriveHmacKey("PLDN-ENTRY-SHARE-SECRET-v1");
        emailKey = keyDeriver.DeriveHmacKey("PLDN-ENTRY-SHARE-EMAIL-v1");
        otpDeliveryKey = keyDeriver.DeriveHmacKey("PLDN-ENTRY-SHARE-OTP-DELIVERY-v1");
        secretHasher = new PasswordHasher<object>(Options.Create(new PasswordHasherOptions
        {
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
            IterationCount = this.options.PasswordHashIterations,
        }));
    }

    internal byte[] HashAccessToken(Guid shareId, string token) =>
        HashToken(accessKey, shareId, token);

    internal string GenerateSessionToken() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    internal byte[] HashSessionToken(Guid sessionId, string token) =>
        HashToken(sessionKey, sessionId, token);

    internal bool VerifyAccessToken(Guid shareId, string token, byte[] expected) =>
        VerifyToken(accessKey, shareId, token, expected);

    internal bool VerifySessionToken(Guid sessionId, string token, byte[] expected) =>
        VerifyToken(sessionKey, sessionId, token, expected);

    internal string? CreateSecretVerifier(Guid shareId, EntryShareProtection protection, string? secret)
    {
        if (protection == EntryShareProtection.None && secret is null)
        {
            return null;
        }

        if (!IsValidSecret(protection, secret))
        {
            throw new DomainException("The sharing protection secret does not satisfy its policy.");
        }

        return secretHasher.HashPassword(HashingContext, PepperSecret(shareId, secret!));
    }

    internal bool VerifySecret(Guid shareId, EntryShareProtection protection, string? secret, string? verifier)
    {
        if (verifier is null || !IsValidSecret(protection, secret))
        {
            return false;
        }

        try
        {
            return secretHasher.VerifyHashedPassword(HashingContext, verifier, PepperSecret(shareId, secret!))
                   != PasswordVerificationResult.Failed;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    internal string GenerateOtp() => RandomNumberGenerator.GetInt32(1_000_000).ToString("D6");

    internal string ProtectOtp(Guid shareId, Guid sessionId, long generation, string otp)
    {
        if (otp.Length != 6 || !otp.All(char.IsAsciiDigit))
        {
            throw new DomainException("The sharing verification code is invalid.");
        }

        var plaintext = Encoding.ASCII.GetBytes(otp);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[6];
        var tag = new byte[16];
        try
        {
            using var aes = new AesGcm(otpDeliveryKey, 16);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, OtpDeliveryContext(shareId, sessionId, generation));
            return WebEncoders.Base64UrlEncode([1, .. nonce, .. tag, .. ciphertext]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    internal string UnprotectOtp(Guid shareId, Guid sessionId, long generation, string protectedOtp)
    {
        var payload = WebEncoders.Base64UrlDecode(protectedOtp);
        if (payload.Length != 35 || payload[0] != 1)
        {
            throw new CryptographicException("Invalid protected sharing code.");
        }

        var plaintext = new byte[6];
        try
        {
            using var aes = new AesGcm(otpDeliveryKey, 16);
            aes.Decrypt(payload.AsSpan(1, 12), payload.AsSpan(29), payload.AsSpan(13, 16),
                plaintext, OtpDeliveryContext(shareId, sessionId, generation));
            return Encoding.ASCII.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] OtpDeliveryContext(Guid shareId, Guid sessionId, long generation)
    {
        if (shareId == Guid.Empty || sessionId == Guid.Empty || generation < 1)
        {
            throw new DomainException("The sharing code requires a session and a positive generation.");
        }

        var context = new byte[40];
        shareId.TryWriteBytes(context.AsSpan(0, 16), bigEndian: true, out _);
        sessionId.TryWriteBytes(context.AsSpan(16, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteInt64BigEndian(context.AsSpan(32), generation);
        return context;
    }

    internal byte[] HashOtp(Guid sessionId, string otp)
    {
        if (otp.Length != 6 || !otp.All(char.IsAsciiDigit))
        {
            throw new DomainException("The sharing verification code is invalid.");
        }

        return ScopedHmac(otpKey, sessionId, Encoding.ASCII.GetBytes(otp));
    }

    internal bool VerifyOtp(Guid sessionId, string otp, byte[]? expected) =>
        expected is { Length: 32 } && otp.Length == 6 && otp.All(char.IsAsciiDigit)
        && CryptographicOperations.FixedTimeEquals(HashOtp(sessionId, otp), expected);

    internal string ProtectRecipientEmail(Guid shareId, string normalizedEmail)
    {
        var plaintext = Encoding.UTF8.GetBytes(normalizedEmail);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        try
        {
            using var aes = new AesGcm(emailKey, tag.Length);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, shareId.ToByteArray(bigEndian: true));
            return WebEncoders.Base64UrlEncode([1, .. nonce, .. tag, .. ciphertext]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    internal string UnprotectRecipientEmail(Guid shareId, string protectedEmail)
    {
        var payload = WebEncoders.Base64UrlDecode(protectedEmail);
        if (payload.Length < 30 || payload[0] != 1)
        {
            throw new CryptographicException("Invalid protected sharing recipient.");
        }

        var plaintext = new byte[payload.Length - 29];
        try
        {
            using var aes = new AesGcm(emailKey, 16);
            aes.Decrypt(payload.AsSpan(1, 12), payload.AsSpan(29), payload.AsSpan(13, 16),
                plaintext, shareId.ToByteArray(bigEndian: true));
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private string PepperSecret(Guid shareId, string secret)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        try
        {
            return Convert.ToBase64String(ScopedHmac(secretPepper, shareId, bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private bool IsValidSecret(EntryShareProtection protection, string? secret) =>
        secret is not null && secret.Length <= options.MaximumSecretLength
        && protection switch
        {
            EntryShareProtection.Password => secret.Length >= options.MinimumPasswordLength,
            EntryShareProtection.Pin => secret.Length >= 6 && secret.All(char.IsAsciiDigit),
            _ => false,
        };

    private static bool VerifyToken(byte[] key, Guid scope, string token, byte[] expected)
    {
        if (token.Length != 43 || expected.Length != 32)
        {
            return false;
        }

        try
        {
            return CryptographicOperations.FixedTimeEquals(HashToken(key, scope, token), expected);
        }
        catch (FormatException)
        {
            return false;
        }
        catch (DomainException)
        {
            return false;
        }
    }

    private static byte[] HashToken(byte[] key, Guid scope, string token)
    {
        if (token.Length != 43)
        {
            throw new DomainException("A sharing token must be canonical unpadded base64url.");
        }

        var bytes = WebEncoders.Base64UrlDecode(token);
        try
        {
            if (bytes.Length != 32 || WebEncoders.Base64UrlEncode(bytes) != token)
            {
                throw new DomainException("A sharing token must be canonical unpadded base64url.");
            }

            return ScopedHmac(key, scope, bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static byte[] ScopedHmac(byte[] key, Guid scope, byte[] value)
    {
        var input = new byte[16 + value.Length];
        scope.TryWriteBytes(input.AsSpan(0, 16), bigEndian: true, out _);
        value.CopyTo(input, 16);
        try
        {
            return HMACSHA256.HashData(key, input);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
    }
}
