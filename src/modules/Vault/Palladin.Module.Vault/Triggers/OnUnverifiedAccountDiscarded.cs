using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.MassTransit;
using Palladin.Module.Vault.Infrastructure.Persistence;
using JetBrains.Annotations;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnUnverifiedAccountDiscardedDefinition : ConsumerDefinition<OnUnverifiedAccountDiscarded>
{
    public OnUnverifiedAccountDiscardedDefinition() => EndpointName = VaultEndpoints.FromIdentity;
}

[UsedImplicitly]
internal sealed class OnUnverifiedAccountDiscarded(VaultDomainWriteContext domainWriteContext)
    : IConsumer<UnverifiedAccountDiscardedEvent>
{
    public async Task Consume(ConsumeContext<UnverifiedAccountDiscardedEvent> context)
    {
        var userId = context.Message.UserId;
        var ct = context.CancellationToken;

        // Vault membership requires a verified e-mail; a member's directory key must never disappear.
        if (await domainWriteContext.VaultMembers.AnyAsync(member => member.UserId == userId, ct))
        {
            return;
        }

        domainWriteContext.RemoveRange([
            .. await domainWriteContext.Users.Where(user => user.Id == userId).ToListAsync(ct),
            .. await domainWriteContext.MemberKeyDirectory.Where(entry => entry.UserId == userId).ToListAsync(ct),
        ]);
        await domainWriteContext.CommitAsync(ct);
    }
}
