using Palladin.Module.Notification.Domain;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Palladin.Module.Notification.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class SuppressedEmailConfiguration : IEntityTypeConfiguration<SuppressedEmail>
{
    public void Configure(EntityTypeBuilder<SuppressedEmail> builder)
    {
        // Normalized address is the natural key — enforces idempotent suppression at the database level.
        builder.HasKey(x => x.Address);
        builder.Property(x => x.Address).HasMaxLength(320);
    }
}
