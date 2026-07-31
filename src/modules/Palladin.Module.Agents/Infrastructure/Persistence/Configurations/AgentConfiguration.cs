using Palladin.Module.Agents.Domain;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Palladin.Module.Agents.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class AgentConfiguration : IEntityTypeConfiguration<Agent>
{
    public void Configure(EntityTypeBuilder<Agent> builder)
    {
        builder.ToTable("agents");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.OrganizationId).HasColumnName("organization_id");
        builder.Property(x => x.PublicKey).HasColumnName("public_key").HasMaxLength(512);
        builder.Property(x => x.SigningPublicKey).HasColumnName("signing_public_key").HasMaxLength(512);
        builder.Property(x => x.RecipientKeyVersion)
            .HasColumnName("recipient_key_version")
            .HasDefaultValue(1u);
        builder.Property(x => x.AccessEpoch)
            .HasColumnName("access_epoch")
            .HasDefaultValue(0u);
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(200);
        builder.Property(x => x.Description).HasColumnName("description").HasMaxLength(2000);
        builder.Property(x => x.Type).HasColumnName("type").HasMaxLength(100);
        builder.Property(x => x.IconKey).HasColumnName("icon_key").HasMaxLength(500);
        builder.Property(x => x.IconColor).HasColumnName("icon_color").HasMaxLength(20);
        builder.Property(x => x.Status).HasColumnName("status");
        builder.Property(x => x.LastUsedApiKeyId).HasColumnName("last_used_api_key_id");
        builder.Property(x => x.LastAccessAt).HasColumnName("last_access_at");
        builder.Property(x => x.LastIp).HasColumnName("last_ip").HasMaxLength(45);
        builder.Property(x => x.LastHostname).HasColumnName("last_hostname").HasMaxLength(253);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        builder.Property(x => x.EnrolledAt).HasColumnName("enrolled_at");
        builder.Property(x => x.EnrolledBy).HasColumnName("enrolled_by");
        builder.Property(x => x.DeactivatedAt).HasColumnName("deactivated_at");
        builder.Property(x => x.DeactivatedBy).HasColumnName("deactivated_by");
        builder.Property(x => x.DeactivationRequestId).HasColumnName("deactivation_request_id");
        builder.Property(x => x.ReactivatedAt).HasColumnName("reactivated_at");
        builder.Property(x => x.ReactivatedBy).HasColumnName("reactivated_by");

        // A public key is the agent's global cryptographic identity (it wraps the VK at grant delivery),
        // so it must be unique across the whole table, not merely per organization — a shared key would
        // let a credential be deliverable to the wrong agent identity.
        builder.HasIndex(x => x.PublicKey).IsUnique();

        builder.HasOne(x => x.EnrolledByUser)
            .WithMany()
            .HasForeignKey(x => x.EnrolledBy)
            .HasConstraintName("fk_agents_enrolled_by")
            .HasPrincipalKey(x => x.Id)
            .OnDelete(DeleteBehavior.NoAction)
            .IsRequired(false);

        builder.HasOne(x => x.DeactivatedByUser)
            .WithMany()
            .HasForeignKey(x => x.DeactivatedBy)
            .HasConstraintName("fk_agents_deactivated_by")
            .HasPrincipalKey(x => x.Id)
            .OnDelete(DeleteBehavior.NoAction)
            .IsRequired(false);

        builder.HasOne(x => x.ReactivatedByUser)
            .WithMany()
            .HasForeignKey(x => x.ReactivatedBy)
            .HasConstraintName("fk_agents_reactivated_by")
            .HasPrincipalKey(x => x.Id)
            .OnDelete(DeleteBehavior.NoAction)
            .IsRequired(false);
    }
}
