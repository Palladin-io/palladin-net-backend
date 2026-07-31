using Palladin.Module.Vault.Domain;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class CredentialFailureReportConfiguration : IEntityTypeConfiguration<CredentialFailureReport>
{
    public void Configure(EntityTypeBuilder<CredentialFailureReport> builder)
    {
        builder.HasKey(x => new { x.OrganizationId, x.AgentId, x.VaultId, x.EntryId, x.Id });

        builder.Property(x => x.Code).HasConversion<string>().HasMaxLength(32);

        builder.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
        builder.HasIndex(x => new { x.OrganizationId, x.EntryId });

        builder.HasOne<Agent>()
            .WithMany()
            .HasForeignKey(x => x.AgentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
