using Palladin.Module.Identity.Domain;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Palladin.Module.Identity.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class WaitlistEntryConfiguration : IEntityTypeConfiguration<WaitlistEntry>
{
    public void Configure(EntityTypeBuilder<WaitlistEntry> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Email).IsRequired().HasMaxLength(320);
        builder.Property(x => x.Language).IsRequired().HasMaxLength(2);
        builder.Property(x => x.AudienceType).HasMaxLength(16);
        builder.Property(x => x.AgentFramework).HasMaxLength(32);
        builder.Property(x => x.AgentFrameworkOther)
            .HasMaxLength(WaitlistQualification.AgentFrameworkOtherMaxLength);
        builder.Property(x => x.CredentialedWorkflow)
            .HasMaxLength(WaitlistQualification.CredentialedWorkflowMaxLength);
        builder.Property(x => x.CurrentWorkaround)
            .HasMaxLength(WaitlistQualification.CurrentWorkaroundMaxLength);
        builder.Property(x => x.CampaignSource)
            .HasMaxLength(WaitlistQualification.CampaignSourceMaxLength);
        builder.Property(x => x.PromotionTermsVersion).HasMaxLength(64);
        builder.Property(x => x.TokenHash).IsRequired().HasMaxLength(64);
        builder.Property(x => x.BenefitPlan).HasMaxLength(32);
        builder.Property(x => x.BenefitStatus).HasMaxLength(32);

        builder.HasIndex(x => x.Email).IsUnique();
        builder.HasIndex(x => x.TokenHash);
        builder.HasIndex(x => x.BenefitUserId).IsUnique();
    }
}
