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
        var replicas = await domainWriteContext.Users
            .Where(user => user.Id == context.Message.UserId)
            .ToListAsync(context.CancellationToken);
        domainWriteContext.RemoveRange(replicas);
        await domainWriteContext.CommitAsync(context.CancellationToken);
    }
}
