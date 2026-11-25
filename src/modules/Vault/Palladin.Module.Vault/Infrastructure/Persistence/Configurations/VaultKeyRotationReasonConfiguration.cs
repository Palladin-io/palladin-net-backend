using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

internal sealed class VaultKeyRotationReasonConfiguration : IEntityTypeConfiguration<VaultKeyRotationReason>
{
    public void Configure(EntityTypeBuilder<VaultKeyRotationReason> builder)
    {
        builder.ToTable("VaultKeyRotationReasons");
        builder.HasKey(x => new { x.OrganizationId, x.VaultId, x.RotationId, x.PrincipalType, x.PrincipalId });
        builder.HasOne<VaultKeyRotation>()
            .WithMany(x => x.Reasons)
            .HasForeignKey(x => new { x.OrganizationId, x.VaultId, x.RotationId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
