namespace Palladin.Module.Agents.Infrastructure.AgentAuth;

internal sealed class ApiKeyCacheOptions
{
    public const string Position = "Modules:Agents:ApiKeyCache";
    public TimeSpan Duration { get; init; } = TimeSpan.FromMinutes(5);
}
