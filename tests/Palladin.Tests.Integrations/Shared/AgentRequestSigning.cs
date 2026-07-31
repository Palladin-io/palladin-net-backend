using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;
using Microsoft.AspNetCore.WebUtilities;

namespace Palladin.Tests.Integrations.Shared;

internal sealed class AgentRequestSigning
{
    private readonly Key _key;

    private AgentRequestSigning(Key key) => _key = key;

    public static AgentRequestSigning Generate() =>
        new(Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        }));

    public string PublicKeyBase64 =>
        Convert.ToBase64String(_key.PublicKey.Export(KeyBlobFormat.RawPublicKey));

    public DelegatingHandler CreateSigningHandler(Guid agentId, HttpMessageHandler inner) =>
        new SigningHandler(this, agentId, inner);

    public string SignCanonical(string canonical) =>
        Convert.ToBase64String(SignatureAlgorithm.Ed25519.Sign(_key, Encoding.UTF8.GetBytes(canonical)));

    public string SignBytesBase64Url(byte[] value) =>
        WebEncoders.Base64UrlEncode(SignatureAlgorithm.Ed25519.Sign(_key, value));

    private async Task<(string Timestamp, string Nonce, string Signature)> SignAsync(
        string method, string pathWithQuery, byte[] body)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var canonical = string.Join('\n',
            method.ToUpperInvariant(),
            pathWithQuery,
            timestamp,
            nonce,
            Convert.ToBase64String(SHA256.HashData(body)));

        var signature = SignatureAlgorithm.Ed25519.Sign(_key, Encoding.UTF8.GetBytes(canonical));
        return (timestamp, nonce, Convert.ToBase64String(signature));
    }

    private sealed class SigningHandler(AgentRequestSigning signing, Guid agentId, HttpMessageHandler inner)
        : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
            var pathWithQuery = request.RequestUri!.PathAndQuery;
            var (timestamp, nonce, signature) =
                await signing.SignAsync(request.Method.Method, pathWithQuery, body);

            request.Headers.Remove("X-Agent-Id");
            request.Headers.Remove("X-Agent-Timestamp");
            request.Headers.Remove("X-Agent-Nonce");
            request.Headers.Remove("X-Agent-Signature");
            request.Headers.Add("X-Agent-Id", agentId.ToString());
            request.Headers.Add("X-Agent-Timestamp", timestamp);
            request.Headers.Add("X-Agent-Nonce", nonce);
            request.Headers.Add("X-Agent-Signature", signature);

            return await base.SendAsync(request, ct);
        }
    }
}
