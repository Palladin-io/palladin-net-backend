using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class ScriptExecutionScopeConfiguration : IEntityTypeConfiguration<ScriptExecutionScope>
{
    public void Configure(EntityTypeBuilder<ScriptExecutionScope> builder)
    {
        builder.HasKey(x => new { x.OrganizationId, x.VaultId, x.GrantId, x.EntryId });
        builder.Property(x => x.EntryRevision).IsRequired();
        builder.Property(x => x.IsScript).IsRequired();
        builder.HasIndex(x => new { x.OrganizationId, x.VaultId, x.EntryId, x.GrantId });

        builder.HasOne<VaultEntry>()
            .WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.VaultId, x.EntryId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.VaultId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
