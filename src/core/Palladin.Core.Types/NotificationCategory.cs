using System.Text.Json.Serialization;

namespace Palladin.Core.Types;

public enum NotificationCategory
{
    [JsonStringEnumMemberName("actionRequired")]
    ActionRequired = 1,

    [JsonStringEnumMemberName("update")]
    Update = 2,
}
