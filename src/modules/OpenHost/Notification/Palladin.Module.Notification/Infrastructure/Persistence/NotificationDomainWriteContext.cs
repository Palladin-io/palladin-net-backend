using Palladin.Core.Events;
using Palladin.Core.Persistence;
using Palladin.Module.Notification.Domain;

namespace Palladin.Module.Notification.Infrastructure.Persistence;

internal sealed class NotificationDomainWriteContext(
    NotificationDbWriteContext writeContext,
    IEnumerable<IEventPublisher> eventPublishers)
    : DomainWriteContextBase(writeContext, eventPublishers)
{
    public IQueryable<PushToken> PushTokens => Track<PushToken>();
    public IQueryable<InboxItem> InboxItems => Track<InboxItem>();
    public IQueryable<Scope> Scopes => Track<Scope>();
    public IQueryable<User> Users => Track<User>();
    public IQueryable<NotificationPreference> NotificationPreferences => Track<NotificationPreference>();
    public IQueryable<SuppressedEmail> SuppressedEmails => Track<SuppressedEmail>();
}
