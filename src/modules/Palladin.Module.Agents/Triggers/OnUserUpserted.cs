using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Infrastructure.MassTransit;
using Palladin.Module.Agents.Infrastructure.Persistence;
using Palladin.Module.Identity.Contracts.Events;
using JetBrains.Annotations;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Palladin.Module.Agents.Triggers;

[UsedImplicitly]
internal sealed class OnUserUpsertedDefinition : ConsumerDefinition<OnUserUpserted>
{
    public OnUserUpsertedDefinition() => EndpointName = AgentsEndpoints.FromIdentity;
}

[UsedImplicitly]
internal sealed class OnUserUpserted(
    AgentsDomainWriteContext domainWriteContext,
    IClock clock) : IConsumer<UserUpsertedEvent>
{
    public async Task Consume(ConsumeContext<UserUpsertedEvent> context)
    {
        var msg = context.Message;
        var existing = await domainWriteContext.Users
            .FirstOrDefaultAsync(u => u.Id == msg.UserId, context.CancellationToken);

        if (existing is null)
        {
            domainWriteContext.Add(User.Create(msg.UserId, msg.DisplayName, clock.GetCurrentInstant(), msg.UpdatedAt));
            await domainWriteContext.CommitAsync(context.CancellationToken);
            return;
        }

        if (msg.UpdatedAt <= existing.UpdatedAt)
        {
            return;
        }

        existing.UpdateDisplayName(msg.DisplayName, msg.UpdatedAt);
        await domainWriteContext.CommitAsync(context.CancellationToken);
    }
}
