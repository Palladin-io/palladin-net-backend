using System.Text;
using NodaTime;

namespace Palladin.Module.Agents.Domain;

internal sealed class ApiKeyCredential
{
    public Guid Id { get; private set; }
    public Guid ApiKeyId { get; private set; }
    public Guid AgentId { get; private set; }
    public string KeyHash { get; private set; } = string.Empty;
    // Internal-only diagnostic metadata. This entity is never projected through
    // user-facing API-key or Agent contracts.
    public string KeySuffix { get; private set; } = string.Empty;
    public Instant CreatedAt { get; private set; }

    private ApiKeyCredential() { }

    internal static ApiKeyCredential FromPlaintext(
        Guid id,
        Guid apiKeyId,
        Guid agentId,
        ReadOnlySpan<byte> plaintext,
        Instant now) =>
        new()
        {
            Id = id,
            ApiKeyId = apiKeyId,
            AgentId = agentId,
            KeyHash = ApiKey.HashKey(plaintext),
            KeySuffix = Encoding.ASCII.GetString(plaintext[^4..]),
            CreatedAt = now,
        };
}
