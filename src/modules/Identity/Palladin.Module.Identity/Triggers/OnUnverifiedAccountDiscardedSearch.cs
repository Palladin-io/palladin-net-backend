using JetBrains.Annotations;
using MassTransit;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.MassTransit;
using Palladin.Module.Search.Contracts.Commands;
using Palladin.Module.Search.Contracts.ValueObjects;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class OnUnverifiedAccountDiscardedSearchDefinition : ConsumerDefinition<OnUnverifiedAccountDiscardedSearch>
{
    public OnUnverifiedAccountDiscardedSearchDefinition() => EndpointName = IdentityEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnUnverifiedAccountDiscardedSearch(IPublishEndpoint publishEndpoint)
    : IConsumer<UnverifiedAccountDiscardedEvent>
{
    public Task Consume(ConsumeContext<UnverifiedAccountDiscardedEvent> context)
    {
        var msg = context.Message;
        return publishEndpoint.Publish(new RemoveSearchItemCommand(
            msg.OrganizationId,
            msg.UserId,
            SearchItemTypes.Member,
            msg.OccurredAt), context.CancellationToken);
    }
}
