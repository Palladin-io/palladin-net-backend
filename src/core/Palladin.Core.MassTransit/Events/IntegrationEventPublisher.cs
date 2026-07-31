using Palladin.Core.Events;
using MassTransit;

namespace Palladin.Core.MassTransit.Events;

internal sealed class IntegrationEventPublisher(IPublishEndpoint publishEndpoint) : IEventPublisher
{
    public async Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken) where TEvent : IEvent
    {
        if (@event is not IIntegrationMessage integrationMessage)
        {
            return;
        }

        await publishEndpoint.Publish(
            integrationMessage,
            integrationMessage.GetType(),
            cancellationToken);
    }
}
