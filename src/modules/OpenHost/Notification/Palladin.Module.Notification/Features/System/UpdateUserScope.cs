using Palladin.Module.Notification.Domain;
using Palladin.Core.Types;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Notification.Contracts.ValueObjects;
using Palladin.Module.Notification.Infrastructure.MassTransit;
using Palladin.Module.Notification.Infrastructure.Persistence;
using JetBrains.Annotations;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Notification.Features;

[UsedImplicitly]
internal sealed class UpdateUserScopeConsumerDefinition : ConsumerDefinition<UpdateUserScopeConsumer>
{
    public UpdateUserScopeConsumerDefinition() => EndpointName = NotificationEndpoints.Scope;
}

[UsedImplicitly]
internal sealed class UpdateUserScopeConsumer(
    NotificationDomainWriteContext domainWriteContext)
    : IConsumer<UpdateUserScope>
{
    public async Task Consume(ConsumeContext<UpdateUserScope> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        if (msg.Action == ScopeAction.Delete)
        {
            var scopes = await domainWriteContext.Scopes
                .Where(s => s.OrganizationId == msg.OrganizationId
                            && s.UserId == msg.UserId
                            && s.Type == msg.Type
                            && s.ItemId == msg.ItemId)
                .ToListAsync(ct);

            var items = await domainWriteContext.InboxItems
                .Where(i => i.OrganizationId == msg.OrganizationId
                            && i.UserId == msg.UserId
                            && i.ScopeType == msg.Type
                            && i.ScopeItemId == msg.ItemId)
                .ToListAsync(ct);

            domainWriteContext.RemoveRange(scopes);
            domainWriteContext.RemoveRange(items);
            await domainWriteContext.CommitAsync(ct);
            return;
        }

        var exists = await domainWriteContext.Scopes
            .AnyAsync(s => s.OrganizationId == msg.OrganizationId
                           && s.UserId == msg.UserId
                           && s.Type == msg.Type
                           && s.ItemId == msg.ItemId, ct);
        if (exists)
        {
            return;
        }

        domainWriteContext.Add(Scope.Create(msg.OrganizationId, msg.UserId, msg.Type, msg.ItemId));
        await domainWriteContext.CommitAsync(ct);
    }
}
