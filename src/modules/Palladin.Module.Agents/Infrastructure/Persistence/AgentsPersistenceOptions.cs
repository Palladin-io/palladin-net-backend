using Palladin.Core.Persistence;

namespace Palladin.Module.Agents.Infrastructure.Persistence;

internal sealed class AgentsPersistenceOptions : IPersistenceOptions
{
    public const string Position = "Modules:Agents:Persistence";
    public string ConnectionString { get; init; } = string.Empty;
    public bool AutoMigration { get; init; }
}
