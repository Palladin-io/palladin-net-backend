using Palladin.Core.Types;

namespace Palladin.Module.Notification.Domain;

internal readonly record struct EffectivePreference(bool Inbox, bool Realtime, bool Push);

internal static class NotificationDefaults
{
    private static readonly HashSet<NotificationType> MandatoryTypes =
    [
        NotificationType.AgentPending,
        NotificationType.GrantPending,
    ];

    private static readonly HashSet<NotificationType> PushOnByDefault =
    [
        NotificationType.AgentPending,
        NotificationType.GrantPending,
        NotificationType.CredentialStale,
    ];

    // Types that carry an in-app action (Approve / Deny) and drive the To-do
    // count. Distinct from PushOnByDefault: CredentialStale pushes but is an
    // informational alert (no inline action), so it must NOT inflate To-do.
    private static readonly HashSet<NotificationType> ActionRequiredTypes =
    [
        NotificationType.AgentPending,
        NotificationType.GrantPending,
    ];

    internal static bool IsMandatory(NotificationType type) => MandatoryTypes.Contains(type);

    internal static bool IsUserFacing(NotificationType type) =>
        type is not (NotificationType.AgentResolved or NotificationType.AgentApproved);

    internal static bool IsInvisibleMarker(NotificationType type) =>
        type is NotificationType.AgentResolved;

    internal static NotificationCategory CategoryFor(NotificationType type) =>
        ActionRequiredTypes.Contains(type) ? NotificationCategory.ActionRequired : NotificationCategory.Update;

    internal static EffectivePreference Resolve(NotificationType type, NotificationPreference? stored)
    {
        var mandatory = IsMandatory(type);

        if (stored is null)
        {
            return new EffectivePreference(Inbox: true, Realtime: true, Push: PushOnByDefault.Contains(type));
        }

        return new EffectivePreference(
            Inbox: mandatory || stored.InboxEnabled,
            Realtime: mandatory || stored.SignalREnabled,
            Push: stored.PushEnabled);
    }
}
