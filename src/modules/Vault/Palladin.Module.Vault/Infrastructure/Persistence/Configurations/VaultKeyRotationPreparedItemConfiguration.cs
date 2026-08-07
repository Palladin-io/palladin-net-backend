using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

internal sealed class VaultKeyRotationPreparedItemConfiguration : IEntityTypeConfiguration<VaultKeyRotationPreparedItem>
{
    public void Configure(EntityTypeBuilder<VaultKeyRotationPreparedItem> builder)
    {
        builder.HasKey(x => new
        {
            x.OrganizationId,
            x.VaultId,
            x.RotationId,
            x.Kind,
            x.SubjectId,
            x.SubjectVersion,
        });
        builder.Property(x => x.Kind).HasConversion<int>();
        builder.Property(x => x.SubjectVersion).HasColumnType("numeric(20,0)");
        builder.Property(x => x.SourceRevision).HasColumnType("numeric(20,0)");
        builder.Property(x => x.Payload).HasMaxLength(VaultProtocol.MaximumRotationPreparedPayloadBytes);
        builder.Property(x => x.PayloadDigest).HasMaxLength(32);
        builder.Property(x => x.PreparedAt);
        builder.HasIndex(x => new { x.OrganizationId, x.VaultId, x.RotationId, x.Kind });
        builder.HasOne<VaultKeyRotation>()
            .WithMany(x => x.PreparedItems)
            .HasForeignKey(x => new { x.OrganizationId, x.VaultId, x.RotationId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
