namespace Palladin.Core.Events;

public interface IEventEntity
{
    IReadOnlyCollection<IEvent> PeekEvents();
    ICollection<IEvent> FetchEvents();
}

public abstract class EventEntityBase : IEventEntity
{
    private ICollection<IEvent> _events = new List<IEvent>();

    protected void AddEvent(IEvent @event) => _events.Add(@event);

    protected void AddOrReplaceEvent(IEvent @event)
    {
        var existingEvent = _events.FirstOrDefault(e => e.GetType() == @event.GetType());
        if (existingEvent != null)
        {
            _events.Remove(existingEvent);
        }

        _events.Add(@event);
    }

    public ICollection<IEvent> FetchEvents()
    {
        var events = _events.ToArray();

        _events.Clear();

        return events;
    }

    public IReadOnlyCollection<IEvent> PeekEvents() => _events.ToArray();
}
