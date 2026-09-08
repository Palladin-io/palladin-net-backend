using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Agents.Domain;

namespace Palladin.Module.Agents.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class ApiKeyCredentialConfiguration : IEntityTypeConfiguration<ApiKeyCredential>
{
    public void Configure(EntityTypeBuilder<ApiKeyCredential> builder)
    {
        builder.ToTable("api_key_credentials");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.ApiKeyId).HasColumnName("api_key_id");
        builder.Property(x => x.AgentId).HasColumnName("agent_id");
        builder.Property(x => x.KeyHash).HasColumnName("key_hash").HasColumnType("text");
        builder.Property(x => x.KeySuffix).HasColumnName("key_suffix").HasColumnType("text");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");

        builder.HasIndex(x => x.KeyHash).IsUnique();
        builder.HasIndex(x => x.AgentId).IsUnique();
        builder.HasOne<ApiKey>()
            .WithMany()
            .HasForeignKey(x => x.ApiKeyId)
            .HasConstraintName("fk_api_key_credentials_api_key")
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Agent>()
            .WithMany()
            .HasForeignKey(x => x.AgentId)
            .HasConstraintName("fk_api_key_credentials_agent")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
