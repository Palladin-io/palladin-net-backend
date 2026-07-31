using Microsoft.AspNetCore.Authentication;

namespace Palladin.Module.Agents.Infrastructure.AgentAuth;

public sealed class AgentAuthenticationOptions : AuthenticationSchemeOptions
{
    public const string SchemeName = "AgentKey";

    public const string ApiKeyHeader = "X-Api-Key";
    public const string AgentKeyHeader = "X-Agent-Key";
    public const string AgentSigningKeyHeader = "X-Agent-Signing-Key";
    public const string AgentIdHeader = "X-Agent-Id";
    public const string AgentNameHeader = "X-Agent-Name";
    public const string AgentTypeHeader = "X-Agent-Type";
    public const string AgentHostnameHeader = "X-Agent-Hostname";

    public const string AgentTimestampHeader = "X-Agent-Timestamp";
    public const string AgentNonceHeader = "X-Agent-Nonce";
    public const string AgentSignatureHeader = "X-Agent-Signature";
}
