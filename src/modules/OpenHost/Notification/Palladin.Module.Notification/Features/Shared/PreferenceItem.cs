using Palladin.Core.Types;
using Palladin.Module.Notification.Domain;
using JetBrains.Annotations;

namespace Palladin.Module.Notification.Features;

[PublicAPI]
public sealed record PreferenceItem(
    NotificationType Type,
    NotificationCategory Category,
    bool InboxEnabled,
    bool SignalREnabled,
    bool PushEnabled,
    bool Mandatory);

internal static class PreferenceItemFactory
{
    internal static IReadOnlyList<PreferenceItem> BuildAll(
        IReadOnlyDictionary<NotificationType, NotificationPreference> stored)
    {
        return Enum.GetValues<NotificationType>()
            .Where(NotificationDefaults.IsUserFacing)
            .Where(type => type != NotificationType.EntryShareReceived)
            .OrderBy(t => (int)t)
            .Select(type =>
            {
                stored.TryGetValue(type, out var pref);
                var effective = NotificationDefaults.Resolve(type, pref);
                return new PreferenceItem(
                    type,
                    NotificationDefaults.CategoryFor(type),
                    effective.Inbox,
                    effective.Realtime,
                    effective.Push,
                    NotificationDefaults.IsMandatory(type));
            })
            .ToList();
    }
}
