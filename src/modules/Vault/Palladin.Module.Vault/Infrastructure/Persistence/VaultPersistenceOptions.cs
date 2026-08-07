using Palladin.Core.Persistence;

namespace Palladin.Module.Vault.Infrastructure.Persistence;

internal sealed class VaultPersistenceOptions : IPersistenceOptions
{
    public const string Position = "Modules:Vault:Persistence";
    public string ConnectionString { get; init; } = string.Empty;
    public bool AutoMigration { get; init; }
}
