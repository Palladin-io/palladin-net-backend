namespace Palladin.Core.Events;

public interface IEventPublisher
{
    Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken) where TEvent : IEvent;
}

public static class EventPublisherExtensions
{
    public static async Task PublishAsync<TEvent>(
        this IEnumerable<IEventPublisher> publishers,
        TEvent @event,
        CancellationToken cancellationToken)
        where TEvent : IEvent
    {
        foreach (var publisher in publishers)
        {
            await publisher.PublishAsync(@event, cancellationToken);
        }
    }
}
