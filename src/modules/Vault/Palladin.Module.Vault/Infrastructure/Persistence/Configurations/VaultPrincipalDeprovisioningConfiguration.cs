using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

internal sealed class VaultPrincipalDeprovisioningConfiguration
    : IEntityTypeConfiguration<VaultPrincipalDeprovisioning>
{
    public void Configure(EntityTypeBuilder<VaultPrincipalDeprovisioning> builder)
    {
        builder.HasKey(x => new { x.OrganizationId, x.Id });
        builder.HasIndex(x => new { x.OrganizationId, x.PrincipalType, x.PrincipalId })
            .IsUnique()
            .HasFilter($"\"Status\" <> {(int)VaultPrincipalDeprovisioningStatus.Completed}");
        builder.HasIndex(x => x.OrganizationId)
            .IsUnique()
            .HasFilter($"\"Status\" = {(int)VaultPrincipalDeprovisioningStatus.WaitingForRotation}");
        builder.HasIndex(x => new { x.OrganizationId, x.RequestedAt, x.Id });
        builder.Property(x => x.PrincipalType);
        builder.Property(x => x.Status);
        builder.Property(x => x.CompletedVaultCount);
        builder.Property(x => x.UpdatedAt).IsConcurrencyToken();
    }
}
