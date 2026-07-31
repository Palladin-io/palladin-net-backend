using Palladin.Module.Audit.Domain;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Palladin.Module.Audit.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class AuditExportJobConfiguration : IEntityTypeConfiguration<AuditExportJob>
{
    public void Configure(EntityTypeBuilder<AuditExportJob> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.StorageKey).HasMaxLength(512);
        builder.Property(x => x.Error).HasMaxLength(2000);

        builder.HasIndex(x => new { x.OrganizationId, x.Id });
    }
}
