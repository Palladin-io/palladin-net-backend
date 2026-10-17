using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class EntryShareSenderAuthorityConfiguration : IEntityTypeConfiguration<EntryShareSenderAuthority>
{
    public void Configure(EntityTypeBuilder<EntryShareSenderAuthority> builder)
    {
        builder.ToTable("EntryShareSenderAuthorities");
        builder.HasKey(x => new { x.OrganizationId, x.UserId });
        builder.Property(x => x.MutationVersion).IsConcurrencyToken();
    }
}

[UsedImplicitly]
internal sealed class EntryShareCreationChallengeConfiguration : IEntityTypeConfiguration<EntryShareCreationChallenge>
{
    public void Configure(EntityTypeBuilder<EntryShareCreationChallenge> builder)
    {
        builder.ToTable("EntryShareCreationChallenges");
        builder.HasKey(x => new { x.OrganizationId, x.VaultId, x.EntryId, x.RequestedBy });
        builder.Property(x => x.MutationVersion).IsConcurrencyToken();
        builder.HasOne<VaultEntry>().WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.VaultId, x.EntryId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
