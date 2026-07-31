using Palladin.Core.Persistence;

namespace Palladin.Module.Search.Infrastructure.Persistence;

internal sealed class SearchPersistenceOptions : IPersistenceOptions
{
    public const string Position = "Modules:Search:Persistence";
    public string ConnectionString { get; init; } = string.Empty;
    public bool AutoMigration { get; init; }
}
