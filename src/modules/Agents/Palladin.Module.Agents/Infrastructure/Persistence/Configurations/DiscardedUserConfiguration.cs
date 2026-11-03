using Palladin.Module.Agents.Domain;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Palladin.Module.Agents.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class DiscardedUserConfiguration : IEntityTypeConfiguration<DiscardedUser>
{
    public void Configure(EntityTypeBuilder<DiscardedUser> builder)
    {
        builder.ToTable("agent_discarded_users");

        builder.HasKey(x => x.UserId);

        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.DiscardedAt).HasColumnName("discarded_at");
    }
}
