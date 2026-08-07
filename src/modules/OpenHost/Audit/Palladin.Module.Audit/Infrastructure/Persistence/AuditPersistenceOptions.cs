using Palladin.Core.Persistence;

namespace Palladin.Module.Audit.Infrastructure.Persistence;

internal sealed class AuditPersistenceOptions : IPersistenceOptions
{
    public const string Position = "Modules:Audit:Persistence";
    public string ConnectionString { get; init; } = string.Empty;
    public bool AutoMigration { get; init; }
}
