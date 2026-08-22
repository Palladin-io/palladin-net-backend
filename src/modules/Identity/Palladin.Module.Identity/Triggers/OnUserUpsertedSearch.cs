using JetBrains.Annotations;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.MassTransit;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Search.Contracts.Commands;
using Palladin.Module.Search.Contracts.ValueObjects;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class OnUserUpsertedSearchDefinition : ConsumerDefinition<OnUserUpsertedSearch>
{
    public OnUserUpsertedSearchDefinition() => EndpointName = IdentityEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnUserUpsertedSearch(
    IPublishEndpoint publishEndpoint,
    IdentityDomainReadContext domainReadContext) : IConsumer<UserUpsertedEvent>
{
    public async Task Consume(ConsumeContext<UserUpsertedEvent> context)
    {
        var msg = context.Message;
        var organizationIds = await domainReadContext.OrganizationMembers
            .Where(member => member.UserId == msg.UserId && member.Status == OrganizationMemberStatus.Active)
            .Select(member => member.OrganizationId)
            .ToListAsync(context.CancellationToken);

        foreach (var organizationId in organizationIds)
        {
            await publishEndpoint.Publish(new IndexSearchItemCommand(
                organizationId,
                msg.UserId,
                SearchItemTypes.Member,
                msg.DisplayName,
                [msg.Email],
                msg.UpdatedAt), context.CancellationToken);
        }
    }
}
