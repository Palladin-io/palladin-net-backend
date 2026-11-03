using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class ApproverKeyChallengeConfiguration : IEntityTypeConfiguration<ApproverKeyChallenge>
{
    public void Configure(EntityTypeBuilder<ApproverKeyChallenge> builder)
    {
        builder.HasKey(x => new { x.OrganizationId, x.IssuedTo });
        builder.Property(x => x.ApproverSetRevision).HasConversion(x => (decimal)x, x => (ulong)x).HasPrecision(20, 0);
        builder.Property(x => x.ApproverSetDigest).HasMaxLength(32).IsRequired();
        builder.Property(x => x.Id).IsConcurrencyToken();
    }
}
