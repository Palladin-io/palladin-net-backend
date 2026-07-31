using Microsoft.EntityFrameworkCore;

namespace Palladin.Core.Persistence;

public abstract class DomainReadContextBase(DbContext readContext)
{
    protected IQueryable<TEntity> Query<TEntity>() where TEntity : class => readContext.Set<TEntity>();

    public IQueryable<TResult> SqlQuery<TResult>(FormattableString query) =>
        readContext.Database.SqlQuery<TResult>(query);
}
