using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class AgentEntrySubmissionConfiguration : IEntityTypeConfiguration<AgentEntrySubmission>
{
    public void Configure(EntityTypeBuilder<AgentEntrySubmission> builder)
    {
        builder.ToTable("AgentEntrySubmissions");
        builder.HasKey(x => new { x.OrganizationId, x.Id });
        builder.Property(x => x.ExpectedEntryRevision)
            .HasConversion(x => (decimal?)x, x => (ulong?)x)
            .HasPrecision(20, 0);
        builder.Property(x => x.SubmissionRevision).HasConversion(x => (decimal)x, x => (ulong)x).HasPrecision(20, 0);
        builder.Property(x => x.Document).HasMaxLength(AgentSubmissionLimits.MaximumDocumentLength);
        builder.Property(x => x.SubmittedDigest).HasMaxLength(32).IsRequired();
        builder.Property(x => x.Version)
            .HasConversion(x => (decimal)x, x => (ulong)x)
            .HasPrecision(20, 0)
            .IsConcurrencyToken();
        builder.HasIndex(x => new { x.OrganizationId, x.State, x.CreatedAt });
        builder.HasIndex(x => new { x.OrganizationId, x.AgentId, x.CreatedAt });
        builder.HasIndex(x => x.TrashedAt)
            .HasFilter($"\"State\" = {(int)AgentSubmissionState.Trashed}");
    }
}
