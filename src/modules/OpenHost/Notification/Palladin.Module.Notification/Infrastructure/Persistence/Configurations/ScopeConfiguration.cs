using Palladin.Module.Notification.Domain;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Palladin.Module.Notification.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class ScopeConfiguration : IEntityTypeConfiguration<Scope>
{
    public void Configure(EntityTypeBuilder<Scope> builder)
    {
        builder.HasKey(x => new { x.OrganizationId, x.UserId, x.Type, x.ItemId });

        builder.Property(x => x.Type).HasMaxLength(64);

        builder.HasIndex(x => new { x.OrganizationId, x.Type, x.ItemId });
    }
}
