using Palladin.Core.Persistence;
using Palladin.Module.Audit.Domain;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Audit.Infrastructure.Persistence;

internal sealed class AuditDbReadContext(DbContextOptions<AuditDbReadContext> options) : DbContext(options)
{
    public DbSet<AuditLogEntry> AuditLogEntries => Set<AuditLogEntry>();
    public DbSet<AuditExportJob> AuditExportJobs => Set<AuditExportJob>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AuditDbWriteContext).Assembly);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        throw new ReadOnlyContextSaveChangesException(nameof(AuditDbReadContext));
}
