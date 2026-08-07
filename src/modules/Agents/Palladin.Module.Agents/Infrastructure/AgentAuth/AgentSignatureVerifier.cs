using System.Security.Cryptography;
using System.Text;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NodaTime;
using NSec.Cryptography;

namespace Palladin.Module.Agents.Infrastructure.AgentAuth;

internal enum AgentSignatureResult
{
    Valid = 0,
    MissingHeaders = 1,
    MalformedHeader = 2,
    TimestampOutOfWindow = 3,
    Replayed = 4,
    InvalidSignature = 5,
}

[UsedImplicitly]
internal sealed class AgentSignatureVerifier(
    IMemoryCache cache,
    IOptions<AgentSignatureOptions> options,
    IClock clock)
{
    private const int MaxNonceBytes = 64;

    private static string ReplayCacheKey(Guid agentId, string nonce) => $"agentsig:{agentId:N}:{nonce}";

    public async Task<AgentSignatureResult> VerifyAsync(
        HttpRequest request,
        Guid agentId,
        string storedSigningPublicKey,
        CancellationToken ct)
    {
        if (!TryReadHeader(request, AgentAuthenticationOptions.AgentTimestampHeader, out var timestampRaw)
            || !TryReadHeader(request, AgentAuthenticationOptions.AgentNonceHeader, out var nonce)
            || !TryReadHeader(request, AgentAuthenticationOptions.AgentSignatureHeader, out var signatureRaw))
        {
            return AgentSignatureResult.MissingHeaders;
        }

        if (!long.TryParse(timestampRaw, out var timestamp)
            || !TryDecodeBase64(signatureRaw, 64, out var signature)
            || !TryDecodeBase64(nonce, MaxNonceBytes, out _)
            || !TryDecodePublicKey(storedSigningPublicKey, out var publicKey))
        {
            return AgentSignatureResult.MalformedHeader;
        }

        var skew = Math.Abs(clock.GetCurrentInstant().ToUnixTimeSeconds() - timestamp);
        if (skew > options.Value.ClockSkewSeconds)
        {
            return AgentSignatureResult.TimestampOutOfWindow;
        }

        var canonical = await BuildCanonicalAsync(request, timestampRaw, nonce, ct);
        var canonicalBytes = Encoding.UTF8.GetBytes(canonical);

        var key = PublicKey.Import(SignatureAlgorithm.Ed25519, publicKey, KeyBlobFormat.RawPublicKey);
        if (!SignatureAlgorithm.Ed25519.Verify(key, canonicalBytes, signature))
        {
            return AgentSignatureResult.InvalidSignature;
        }

        var replayKey = ReplayCacheKey(agentId, nonce);
        if (cache.TryGetValue(replayKey, out _))
        {
            return AgentSignatureResult.Replayed;
        }

        cache.Set(replayKey, true, TimeSpan.FromSeconds(options.Value.NonceTtlSeconds));
        return AgentSignatureResult.Valid;
    }

    private static async Task<string> BuildCanonicalAsync(
        HttpRequest request, string timestamp, string nonce, CancellationToken ct)
    {
        var method = request.Method.ToUpperInvariant();
        var pathWithQuery = request.Path.Value + request.QueryString.Value;
        var bodyHash = await HashBodyAsync(request, ct);

        return string.Join('\n', method, pathWithQuery, timestamp, nonce, bodyHash);
    }

    private static async Task<string> HashBodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.Body.CanSeek)
        {
            request.Body.Position = 0;
        }

        using var sha = SHA256.Create();
        await using var crypto = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write);
        await request.Body.CopyToAsync(crypto, ct);
        await crypto.FlushFinalBlockAsync(ct);

        if (request.Body.CanSeek)
        {
            request.Body.Position = 0;
        }

        return Convert.ToBase64String(sha.Hash!);
    }

    private static bool TryReadHeader(HttpRequest request, string name, out string value)
    {
        value = request.Headers[name].ToString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryDecodeBase64(string value, int maxBytes, out byte[] bytes)
    {
        bytes = [];
        Span<byte> buffer = stackalloc byte[maxBytes];
        if (!Convert.TryFromBase64String(value, buffer, out var written))
        {
            return false;
        }

        bytes = buffer[..written].ToArray();
        return true;
    }

    private static bool TryDecodePublicKey(string value, out byte[] bytes)
    {
        if (!TryDecodeBase64(value, 32, out bytes))
        {
            return false;
        }

        return bytes.Length == 32;
    }
}
