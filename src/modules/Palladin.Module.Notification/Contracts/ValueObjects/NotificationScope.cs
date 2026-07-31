using JetBrains.Annotations;

namespace Palladin.Module.Notification.Contracts.ValueObjects;

[PublicAPI]
public sealed record NotificationScope(string Type, Guid ItemId);
