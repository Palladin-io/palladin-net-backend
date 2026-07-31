namespace Palladin.Core.Persistence;

public interface IPersistenceOptions
{
    public string ConnectionString { get; init; }
    public bool AutoMigration { get; init; }
}
