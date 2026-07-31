using Palladin.Core.Persistence;

namespace Palladin.Module.Identity.Infrastructure.Persistence;

internal sealed class IdentityPersistenceOptions : IPersistenceOptions
{
    public const string Position = "Modules:Identity:Persistence";
    public string ConnectionString { get; init; } = string.Empty;
    public bool AutoMigration { get; init; }
}
