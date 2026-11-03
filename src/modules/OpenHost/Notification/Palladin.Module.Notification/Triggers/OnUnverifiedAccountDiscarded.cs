using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Notification.Domain;
using Palladin.Module.Notification.Infrastructure.MassTransit;
using Palladin.Module.Notification.Infrastructure.Persistence;
using JetBrains.Annotations;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Notification.Triggers;

[UsedImplicitly]
internal sealed class OnUnverifiedAccountDiscardedDefinition : ConsumerDefinition<OnUnverifiedAccountDiscarded>
{
    public OnUnverifiedAccountDiscardedDefinition() => EndpointName = NotificationEndpoints.FromIdentity;
}

[UsedImplicitly]
internal sealed class OnUnverifiedAccountDiscarded(NotificationDomainWriteContext domainWriteContext)
    : IConsumer<UnverifiedAccountDiscardedEvent>
{
    public async Task Consume(ConsumeContext<UnverifiedAccountDiscardedEvent> context)
    {
        var userId = context.Message.UserId;
        var ct = context.CancellationToken;

        if (await domainWriteContext.DiscardedUsers.AnyAsync(discarded => discarded.UserId == userId, ct))
        {
            return;
        }

        domainWriteContext.Add(DiscardedUser.Create(userId, context.Message.OccurredAt));
        domainWriteContext.RemoveRange([
            .. await domainWriteContext.Users.Where(user => user.UserId == userId).ToListAsync(ct),
            .. await domainWriteContext.Scopes.Where(scope => scope.UserId == userId).ToListAsync(ct),
            .. await domainWriteContext.InboxItems.Where(item => item.UserId == userId).ToListAsync(ct),
            .. await domainWriteContext.NotificationPreferences.Where(preference => preference.UserId == userId).ToListAsync(ct),
            .. await domainWriteContext.PushTokens.Where(token => token.UserId == userId).ToListAsync(ct),
        ]);
        await domainWriteContext.CommitAsync(ct);
    }
}
