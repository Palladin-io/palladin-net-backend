using System.Linq.Expressions;
using Palladin.Core.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;

namespace Palladin.Core.Persistence;

public abstract class DomainWriteContextBase(
    DbContext writeContext,
    IEnumerable<IEventPublisher> eventPublisher)
{
    private readonly List<IEvent> _pendingEvents = [];

    protected IQueryable<TEntity> Track<TEntity>() where TEntity : class => writeContext.Set<TEntity>();

    protected IEnumerable<TEntity> Tracked<TEntity>() where TEntity : class =>
        writeContext.ChangeTracker.Entries<TEntity>().Select(x => x.Entity);

    public void Add(object entity) => writeContext.Add(entity);

    public void AddRange(IEnumerable<object> entity) => writeContext.AddRange(entity);

    public void Update(object entity) => writeContext.Update(entity);

    public void MarkPropertyAsUpdated<TEntity>(TEntity entity, Expression<Func<TEntity, object>> selector)
        where TEntity : class
    {
        var entry = writeContext.Entry(entity);

        writeContext.Set<TEntity>().Attach(entity);
        entry.Property(selector).IsModified = true;
    }

    public void RemoveRange(IEnumerable<object> entity) => writeContext.RemoveRange(entity);

    public IQueryable<TResult> SqlQuery<TResult>(FormattableString query) =>
        writeContext.Database.SqlQuery<TResult>(query);

    public IQueryable<TEntity> FromSqlInterpolated<TEntity>(FormattableString query)
        where TEntity : class =>
        writeContext.Set<TEntity>().FromSqlInterpolated(query);

    public Task<int> ExecuteSqlInterpolatedAsync(
        FormattableString command,
        CancellationToken cancellationToken = default) =>
        writeContext.Database.ExecuteSqlInterpolatedAsync(command, cancellationToken);

    public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
        writeContext.Database.BeginTransactionAsync(cancellationToken);

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        var changes = writeContext.ChangeTracker.Entries().ToList();
        var events = PeekEvents(changes);
        await PrepareEventsAsync(events, cancellationToken);
        await writeContext.SaveChangesAsync(cancellationToken);
        events = GetEvents(changes);
        events.InsertRange(0, DrainPendingEvents());

        await PublishEventsAsync(events, cancellationToken);
    }

    public async Task CommitAsync(
        IDbContextTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        var changes = writeContext.ChangeTracker.Entries().ToList();
        var events = PeekEvents(changes);
        await PrepareEventsAsync(events, cancellationToken);
        await writeContext.SaveChangesAsync(cancellationToken);
        events = GetEvents(changes);
        events.InsertRange(0, DrainPendingEvents());
        await transaction.CommitAsync(cancellationToken);

        await PublishEventsAsync(events, cancellationToken);
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        var changes = writeContext.ChangeTracker.Entries().ToList();
        var events = PeekEvents(changes);
        await PrepareEventsAsync(events, cancellationToken);
        await writeContext.SaveChangesAsync(cancellationToken);
        events = GetEvents(changes);
        _pendingEvents.AddRange(events);
    }

    private async Task PublishEventsAsync(
        IEnumerable<IEvent> events,
        CancellationToken cancellationToken)
    {
        foreach (var @event in events)
        {
            await HandleEventAsync(@event, cancellationToken);
        }
    }

    public void Clear() => writeContext.ChangeTracker.Clear();

    private static List<IEvent> GetEvents(List<EntityEntry> changes) =>
        changes.Where(x => x.Entity is IEventEntity)
            .SelectMany(
                x =>
                {
                    var events = ((IEventEntity)x.Entity).FetchEvents();

                    return events;
                }
            )
            .ToList();

    private static List<IEvent> PeekEvents(List<EntityEntry> changes) =>
        changes.Where(x => x.Entity is IEventEntity)
            .SelectMany(x => ((IEventEntity)x.Entity).PeekEvents())
            .ToList();

    private List<IEvent> DrainPendingEvents()
    {
        var events = new List<IEvent>(_pendingEvents);
        _pendingEvents.Clear();
        return events;
    }

    protected virtual Task PrepareEventsAsync(
        IReadOnlyCollection<IEvent> events,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    protected virtual async Task HandleEventAsync(IEvent @event, CancellationToken cancellationToken = default)
    {
        foreach (var publisher in eventPublisher)
        {
            await publisher.PublishAsync(@event, cancellationToken);
        }
    }

    public void Remove(object entity) => writeContext.Remove(entity);
}
