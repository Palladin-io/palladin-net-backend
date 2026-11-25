using JetBrains.Annotations;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Module.Identity.Contracts.Commands;
using Palladin.Module.Identity.Infrastructure.MassTransit;
using Palladin.Module.Identity.Infrastructure.Persistence;

namespace Palladin.Module.Identity.Features;

[UsedImplicitly]
internal sealed class DeleteLegacyMemberKeyConsumerDefinition : ConsumerDefinition<DeleteLegacyMemberKeyConsumer>
{
    public DeleteLegacyMemberKeyConsumerDefinition() => EndpointName = IdentityEndpoints.MemberKeys;
}

// A stale or repeated command (other legacy version, already deleted) is a no-op.
[UsedImplicitly]
internal sealed class DeleteLegacyMemberKeyConsumer(IdentityDomainWriteContext domainWriteContext, IClock clock)
    : IConsumer<DeleteLegacyMemberKeyCommand>
{
    public async Task Consume(ConsumeContext<DeleteLegacyMemberKeyCommand> context)
    {
        var msg = context.Message;
        var user = await domainWriteContext.Users.FirstOrDefaultAsync(u => u.Id == msg.UserId, context.CancellationToken);
        if (user?.DeleteLegacyMemberKey(msg.LegacyMemberKeyVersion, clock.GetCurrentInstant()) == true)
        {
            await domainWriteContext.CommitAsync(context.CancellationToken);
        }
    }
}
