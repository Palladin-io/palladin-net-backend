using Palladin.Module.Vault.Domain;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class GrantConfiguration : IEntityTypeConfiguration<Grant>
{
    public void Configure(EntityTypeBuilder<Grant> builder)
    {
        builder.HasKey(x => x.Id);

        // CLR `Type` is a read-only polymorphic domain property (used by Covers() and events), so it
        // cannot back the discriminator. EF manages a dedicated string discriminator column instead.
        builder.HasDiscriminator<string>("GrantType")
            .HasValue<GranularGrant>(Core.Types.GrantType.Granular.ToString())
            .HasValue<FullGrant>(Core.Types.GrantType.Full.ToString());

        builder.Ignore(x => x.Type);

        builder.Property(x => x.AgentPublicKey).HasMaxLength(512);
        builder.Property(x => x.AgentAccessEpoch).IsRequired();
        builder.Property(x => x.ExpirySource).HasMaxLength(16);
        builder.Property(x => x.LastAccessIp).HasMaxLength(45);
        builder.Property(x => x.LastAccessHostname).HasMaxLength(253);

        builder.Property(x => x.UpdatedAt).IsConcurrencyToken();

        builder.HasIndex(x => new { x.VaultId, x.Status });
        builder.HasIndex(x => new { x.AgentId, x.Status });
        builder.HasIndex(x => new { x.AgentId, x.AgentAccessEpoch, x.Status });
        builder.HasIndex(x => new { x.VaultId, x.CreatedAt, x.Id });

        // Org-scoped listing for the grants panel: equality filter + newest-first sort (CreatedAt, Id).
        // One composite per filter dimension so the planner can both filter and order from the index.
        // The status index also serves the unfiltered org listing (OrganizationId prefix).
        builder.HasIndex(x => new { x.OrganizationId, x.Status, x.CreatedAt, x.Id });
        builder.HasIndex(x => new { x.OrganizationId, x.AgentId, x.CreatedAt, x.Id });
        builder.HasIndex(x => new { x.OrganizationId, x.VaultId, x.CreatedAt, x.Id });

        // Entry filter (GRANULAR-only) is indexed on the GranularGrant configuration — EntryId lives on
        // that subtype (TPH). See GranularGrantConfiguration.

        builder.HasMany(x => x.GrantEntryScopes)
            .WithOne()
            .HasForeignKey(x => new { x.OrganizationId, x.VaultId, x.GrantId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.VaultId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.EncryptedReason)
            .WithOne()
            .HasForeignKey<EncryptedReasonEnvelope>(x => new { x.OrganizationId, x.VaultId, x.GrantRequestId })
            .HasPrincipalKey<Grant>(x => new { x.OrganizationId, x.VaultId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Domain.Vault>()
            .WithMany()
            .HasForeignKey(x => x.VaultId)
            .HasPrincipalKey(x => x.Id)
            .OnDelete(DeleteBehavior.Cascade);

        // Real tenant-first FK to the exact Vault-owned Agent replica. Agent identity and access epoch
        // are authorization state, so a Grant cannot exist before enrollment or point across tenants.
        builder.HasOne<Agent>()
            .WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.AgentId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);
    }
}
