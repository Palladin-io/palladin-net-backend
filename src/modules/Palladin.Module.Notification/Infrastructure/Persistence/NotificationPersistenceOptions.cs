using Palladin.Core.Persistence;

namespace Palladin.Module.Notification.Infrastructure.Persistence;

internal sealed class NotificationPersistenceOptions : IPersistenceOptions
{
    public const string Position = "Modules:Notification:Persistence";
    public string ConnectionString { get; init; } = string.Empty;
    public bool AutoMigration { get; init; }
}
