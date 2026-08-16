using Palladin.Module.Notification.Domain;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Palladin.Module.Notification.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class EmailDeliveryConfiguration : IEntityTypeConfiguration<EmailDelivery>
{
    public void Configure(EntityTypeBuilder<EmailDelivery> builder)
    {
        builder.HasKey(delivery => delivery.IdempotencyKey);
        builder.Property(delivery => delivery.IdempotencyKey).HasMaxLength(200);
        builder.Property(delivery => delivery.Status).HasConversion<short>();
        builder.Property(delivery => delivery.UpdatedAt).IsConcurrencyToken();
    }
}
