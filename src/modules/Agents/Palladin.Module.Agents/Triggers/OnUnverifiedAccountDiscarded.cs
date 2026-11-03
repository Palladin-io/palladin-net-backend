using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Infrastructure.MassTransit;
using Palladin.Module.Agents.Infrastructure.Persistence;
using Palladin.Module.Identity.Contracts.Events;
using JetBrains.Annotations;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Agents.Triggers;

[UsedImplicitly]
internal sealed class OnUnverifiedAccountDiscardedDefinition : ConsumerDefinition<OnUnverifiedAccountDiscarded>
{
    public OnUnverifiedAccountDiscardedDefinition() => EndpointName = AgentsEndpoints.FromIdentity;
}

[UsedImplicitly]
internal sealed class OnUnverifiedAccountDiscarded(AgentsDomainWriteContext domainWriteContext)
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
        domainWriteContext.RemoveRange(await domainWriteContext.Users.Where(user => user.Id == userId).ToListAsync(ct));
        await domainWriteContext.CommitAsync(ct);
    }
}
