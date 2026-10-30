using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class ApproverKeyWrapConfiguration : IEntityTypeConfiguration<ApproverKeyWrap>
{
    public void Configure(EntityTypeBuilder<ApproverKeyWrap> builder)
    {
        builder.HasKey(x => new { x.OrganizationId, x.MemberId, x.KeyVersion, x.Purpose });
        builder.Property(x => x.ApproverSetRevision).HasConversion(x => (decimal)x, x => (ulong)x).HasPrecision(20, 0);
        builder.Property(x => x.RecipientKeyVersion)
            .HasConversion(x => (decimal)x.Value, x => new MemberRecipientKeyVersion((uint)x))
            .HasPrecision(10, 0);
        builder.Property(x => x.RecipientFingerprint).HasMaxLength(32).IsRequired();
        builder.Property(x => x.SealedPackage).HasMaxLength(ApproverKeyWrap.SealedPackageBytes).IsRequired();
    }
}
