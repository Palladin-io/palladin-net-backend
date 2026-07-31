namespace Palladin.Module.Agents.Infrastructure.AgentAuth;

internal sealed class AgentSignatureOptions
{
    public const string Position = "Modules:Agents:Signature";

    public int ClockSkewSeconds { get; init; } = 300;

    public int NonceTtlSeconds { get; init; } = 600;
}
