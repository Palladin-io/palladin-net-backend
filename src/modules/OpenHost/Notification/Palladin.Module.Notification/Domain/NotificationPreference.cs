using Palladin.Core.Types;
using NodaTime;

namespace Palladin.Module.Notification.Domain;

internal sealed class NotificationPreference
{
    public Guid OrganizationId { get; private set; }
    public Guid UserId { get; private set; }
    public NotificationType Type { get; private set; }
    public bool InboxEnabled { get; private set; }
    public bool SignalREnabled { get; private set; }
    public bool PushEnabled { get; private set; }
    public Instant UpdatedAt { get; private set; }

    private NotificationPreference() { }

    internal static NotificationPreference Create(
        Guid organizationId,
        Guid userId,
        NotificationType type,
        bool inboxEnabled,
        bool signalREnabled,
        bool pushEnabled,
        Instant now) =>
        new()
        {
            OrganizationId = organizationId,
            UserId = userId,
            Type = type,
            InboxEnabled = inboxEnabled,
            SignalREnabled = signalREnabled,
            PushEnabled = pushEnabled,
            UpdatedAt = now,
        };

    internal void Update(bool inboxEnabled, bool signalREnabled, bool pushEnabled, Instant now)
    {
        InboxEnabled = inboxEnabled;
        SignalREnabled = signalREnabled;
        PushEnabled = pushEnabled;
        UpdatedAt = now;
    }
}
