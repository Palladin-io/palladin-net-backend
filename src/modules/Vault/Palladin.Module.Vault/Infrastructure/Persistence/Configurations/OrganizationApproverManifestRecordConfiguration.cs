using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class OrganizationApproverManifestRecordConfiguration
    : IEntityTypeConfiguration<OrganizationApproverManifestRecord>
{
    public void Configure(EntityTypeBuilder<OrganizationApproverManifestRecord> builder)
    {
        builder.ToTable("OrganizationApproverManifests");
        builder.HasKey(x => new { x.OrganizationId, x.ManifestRevision });
        builder.Property(x => x.ManifestRevision).HasConversion(x => (decimal)x, x => (ulong)x).HasPrecision(20, 0);
        builder.Property(x => x.Document).HasMaxLength(4096).IsRequired();
    }
}
