using Palladin.Core.Security;
using Palladin.Core.Types;
using NodaTime;

namespace Palladin.Module.Notification.Domain;

internal sealed class InboxItem
{
    public Guid OrganizationId { get; private set; }
    public Guid UserId { get; private set; }
    public Guid Id { get; private set; }

    public NotificationType Type { get; private set; }
    public NotificationCategory Category { get; private set; }
    public string TitleKey { get; private set; } = string.Empty;
    public IReadOnlyDictionary<string, string> Metadata { get; private set; } = new Dictionary<string, string>();

    public string ScopeType { get; private set; } = string.Empty;
    public Guid ScopeItemId { get; private set; }

    public Guid SubjectId { get; private set; }

    public bool Collapsible { get; private set; }

    public Permission? RequiredPermission { get; private set; }

    public Instant OccurredAt { get; private set; }
    public Instant? ReadAt { get; private set; }

    private InboxItem() { }

    internal static InboxItem Create(
        Guid id,
        Guid organizationId,
        Guid userId,
        NotificationType type,
        NotificationCategory category,
        string titleKey,
        IReadOnlyDictionary<string, string> metadata,
        string scopeType,
        Guid scopeItemId,
        Guid subjectId,
        bool collapsible,
        Permission? requiredPermission,
        Instant occurredAt) =>
        new()
        {
            Id = id,
            OrganizationId = organizationId,
            UserId = userId,
            Type = type,
            Category = category,
            TitleKey = titleKey,
            Metadata = metadata,
            ScopeType = scopeType,
            ScopeItemId = scopeItemId,
            SubjectId = subjectId,
            Collapsible = collapsible,
            RequiredPermission = requiredPermission,
            OccurredAt = occurredAt,
        };

    internal void MarkRead(Instant now)
    {
        if (ReadAt is null)
        {
            ReadAt = now;
        }
    }
}
