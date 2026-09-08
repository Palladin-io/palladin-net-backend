using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Agents.Domain;

namespace Palladin.Module.Agents.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class AgentDisplayNameFenceConfiguration : IEntityTypeConfiguration<AgentDisplayNameFence>
{
    internal const string PrimaryKey = "PK_agent_display_name_fences";

    public void Configure(EntityTypeBuilder<AgentDisplayNameFence> builder)
    {
        builder.ToTable("agent_display_name_fences");
        builder.HasKey(x => x.OrganizationId).HasName(PrimaryKey);
        builder.Property(x => x.OrganizationId).HasColumnName("organization_id");
        builder.Property(x => x.Revision)
            .HasColumnName("revision")
            .HasDefaultValue(1u)
            .IsConcurrencyToken();
    }
}
