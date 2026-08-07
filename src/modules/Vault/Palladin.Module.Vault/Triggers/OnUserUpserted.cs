using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.MassTransit;
using Palladin.Module.Vault.Infrastructure.Persistence;
using JetBrains.Annotations;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnUserUpsertedDefinition : ConsumerDefinition<OnUserUpserted>
{
    public OnUserUpsertedDefinition() => EndpointName = VaultEndpoints.FromIdentity;
}

[UsedImplicitly]
internal sealed class OnUserUpserted(
    VaultDomainWriteContext domainWriteContext,
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
