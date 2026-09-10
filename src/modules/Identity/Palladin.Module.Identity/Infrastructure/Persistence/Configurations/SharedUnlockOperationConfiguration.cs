using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Identity.Domain;

namespace Palladin.Module.Identity.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class SharedUnlockOperationConfiguration : IEntityTypeConfiguration<SharedUnlockOperation>
{
    public void Configure(EntityTypeBuilder<SharedUnlockOperation> builder)
    {
        builder.HasKey(operation => operation.Id);
        builder.HasIndex(operation => new { operation.ExpiresAt, operation.Id });
        builder.Property(operation => operation.Revision).IsConcurrencyToken();
        builder.HasOne<User>().WithMany().HasForeignKey(operation => operation.UserId);
        builder.Property(operation => operation.ApiOrigin).HasMaxLength(2048);
        builder.Property(operation => operation.WebOrigin).HasMaxLength(2048);
        builder.Property(operation => operation.ExtensionId).HasMaxLength(256);
        builder.Property(operation => operation.DocumentBinding).HasMaxLength(256);
    }
}
