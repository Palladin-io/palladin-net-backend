using Palladin.Module.Search.Domain;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Search.Infrastructure.Persistence;

internal sealed class SearchDbWriteContext(DbContextOptions<SearchDbWriteContext> options) : DbContext(options)
{
    public DbSet<SearchItem> Items => Set<SearchItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SearchDbWriteContext).Assembly);
    }
}
