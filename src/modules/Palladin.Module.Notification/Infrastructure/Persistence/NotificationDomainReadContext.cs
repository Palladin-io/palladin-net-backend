using Palladin.Core.Persistence;
using Palladin.Module.Notification.Domain;

namespace Palladin.Module.Notification.Infrastructure.Persistence;

internal sealed class NotificationDomainReadContext(NotificationDbReadContext readContext)
    : DomainReadContextBase(readContext)
{
    public IQueryable<PushToken> PushTokens => Query<PushToken>();
    public IQueryable<InboxItem> InboxItems => Query<InboxItem>();
    public IQueryable<Scope> Scopes => Query<Scope>();
    public IQueryable<User> Users => Query<User>();
    public IQueryable<NotificationPreference> NotificationPreferences => Query<NotificationPreference>();
    public IQueryable<SuppressedEmail> SuppressedEmails => Query<SuppressedEmail>();
}
