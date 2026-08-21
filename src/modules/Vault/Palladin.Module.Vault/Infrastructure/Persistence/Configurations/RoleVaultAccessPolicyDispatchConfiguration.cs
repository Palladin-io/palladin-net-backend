using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class RoleVaultAccessPolicyDispatchConfiguration
    : IEntityTypeConfiguration<RoleVaultAccessPolicyDispatch>
{
    public void Configure(EntityTypeBuilder<RoleVaultAccessPolicyDispatch> builder)
    {
        builder.HasKey(x => new { x.OrganizationId, x.RoleId });
        builder.Property(x => x.SelectedVaultIds).HasColumnType("uuid[]");
        builder.Property(x => x.Revision)
            .HasConversion(x => (decimal)x, x => (ulong)x)
            .HasPrecision(20, 0)
            .IsConcurrencyToken();
        builder.Property(x => x.PublishedRevision)
            .HasConversion(x => (decimal)x, x => (ulong)x)
            .HasPrecision(20, 0);
        builder.Property(x => x.DispatchKey).HasMaxLength(128);
        builder.Property(x => x.LastErrorCode).HasMaxLength(64);
        builder.HasIndex(x => x.DispatchKey).IsUnique();
        builder.HasIndex(x => new { x.NextAttemptAt, x.OrganizationId, x.RoleId });
    }
}
