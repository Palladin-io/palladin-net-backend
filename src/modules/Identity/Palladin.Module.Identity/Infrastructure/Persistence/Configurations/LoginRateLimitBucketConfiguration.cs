using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Identity.Domain;

namespace Palladin.Module.Identity.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class LoginRateLimitBucketConfiguration : IEntityTypeConfiguration<LoginRateLimitBucket>
{
    public void Configure(EntityTypeBuilder<LoginRateLimitBucket> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.PartitionKey).IsRequired().HasMaxLength(64);
        builder.Property(x => x.Version).IsConcurrencyToken();
        builder.HasIndex(x => x.PartitionKey).IsUnique();
        builder.HasIndex(x => x.UpdatedAt);
    }
}
