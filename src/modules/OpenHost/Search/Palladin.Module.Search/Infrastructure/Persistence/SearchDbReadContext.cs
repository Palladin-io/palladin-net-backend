using Palladin.Core.Persistence;
using Palladin.Module.Search.Domain;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Search.Infrastructure.Persistence;

internal sealed class SearchDbReadContext(DbContextOptions<SearchDbReadContext> options) : DbContext(options)
{
    public DbSet<SearchItem> Items => Set<SearchItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SearchDbWriteContext).Assembly);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        throw new ReadOnlyContextSaveChangesException(nameof(SearchDbReadContext));
}
