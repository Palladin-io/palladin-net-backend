using System.Reflection;
using System.Text.Json.Serialization;
using Palladin.Core.Types;

namespace Palladin.Module.Notification.Shared;

internal static class NotificationTypeWire
{
    private static readonly IReadOnlyDictionary<NotificationType, string> WireByType = BuildMap();

    internal static string ToWire(this NotificationType type) =>
        WireByType.TryGetValue(type, out var wire) ? wire : type.ToString();

    private static Dictionary<NotificationType, string> BuildMap() =>
        Enum.GetValues<NotificationType>()
            .ToDictionary(
                type => type,
                type => typeof(NotificationType)
                            .GetField(type.ToString())!
                            .GetCustomAttribute<JsonStringEnumMemberNameAttribute>()
                            ?.Name
                        ?? type.ToString());
}
