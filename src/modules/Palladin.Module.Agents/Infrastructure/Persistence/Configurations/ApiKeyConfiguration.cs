using Palladin.Module.Agents.Domain;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Palladin.Module.Agents.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class ApiKeyConfiguration : IEntityTypeConfiguration<ApiKey>
{
    public void Configure(EntityTypeBuilder<ApiKey> builder)
    {
        builder.ToTable("api_keys");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.OrganizationId).HasColumnName("organization_id");
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(200);
        builder.Property(x => x.CreatedBy).HasColumnName("created_by");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.RevokedBy).HasColumnName("revoked_by");
        builder.Property(x => x.RevokedAt).HasColumnName("revoked_at");
        builder.Property(x => x.Status).HasColumnName("status");

        builder.Property(x => x.KeyHash)
            .HasColumnName("key_hash")
            .HasColumnType("text");

        builder.Property(x => x.KeySuffix)
            .HasColumnName("key_suffix")
            .HasColumnType("text");

        builder.HasIndex(x => x.KeyHash).IsUnique();
        builder.HasIndex(x => x.OrganizationId);
    }
}
