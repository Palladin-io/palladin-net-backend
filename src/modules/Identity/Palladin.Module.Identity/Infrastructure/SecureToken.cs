using System.Security.Cryptography;
using Palladin.Module.Identity.Infrastructure.Jwt;

namespace Palladin.Module.Identity.Infrastructure;

// A high-entropy, URL-safe opaque token and its SHA-256 hash. Only the hash is persisted; the
// plaintext travels once (email link / challenge response) and is never logged.
internal static class SecureToken
{
    public static (string Token, string TokenHash) Generate()
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        return (token, TokenService.HashToken(token));
    }

    public static string Hash(string token) => TokenService.HashToken(token.Trim());
}
