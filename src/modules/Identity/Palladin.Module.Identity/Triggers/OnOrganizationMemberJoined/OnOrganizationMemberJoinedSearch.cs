using JetBrains.Annotations;
using MassTransit;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.MassTransit;
using Palladin.Module.Search.Contracts.Commands;
using Palladin.Module.Search.Contracts.ValueObjects;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class OnOrganizationMemberJoinedSearchDefinition
    : ConsumerDefinition<OnOrganizationMemberJoinedSearch>
{
    public OnOrganizationMemberJoinedSearchDefinition() => EndpointName = IdentityEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnOrganizationMemberJoinedSearch(IPublishEndpoint publishEndpoint)
    : IConsumer<OrganizationMemberJoinedEvent>
{
    public Task Consume(ConsumeContext<OrganizationMemberJoinedEvent> context)
    {
        var msg = context.Message;
        return publishEndpoint.Publish(new IndexSearchItemCommand(
            msg.OrganizationId,
            msg.UserId,
            SearchItemTypes.Member,
            msg.DisplayName,
            [msg.Email],
            msg.OccurredAt), context.CancellationToken);
    }
}
