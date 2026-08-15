using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Agents.Domain;

namespace Palladin.Module.Agents.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class FormDiscoveryMapConfiguration : IEntityTypeConfiguration<FormDiscoveryMap>
{
    public void Configure(EntityTypeBuilder<FormDiscoveryMap> builder)
    {
        builder.ToTable("form_discovery_maps");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.OrganizationId).HasColumnName("organization_id");
        builder.Property(x => x.SubmittedByAgentId).HasColumnName("submitted_by_agent_id");
        builder.Property(x => x.Domain).HasColumnName("domain").HasMaxLength(253).IsRequired();
        builder.Property(x => x.LoginUrl).HasColumnName("login_url").HasMaxLength(2048).IsRequired();
        builder.Property(x => x.Provider).HasColumnName("provider").HasMaxLength(64).IsRequired();
        builder.Property(x => x.Fingerprint).HasColumnName("fingerprint").HasMaxLength(128).IsRequired();
        builder.Property(x => x.MapVersion).HasColumnName("map_version");
        builder.Property(x => x.DefinitionJson).HasColumnName("definition_json").HasMaxLength(65536).IsRequired();
        builder.Property(x => x.Status).HasColumnName("status");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        builder.HasIndex(x => new { x.OrganizationId, x.Domain, x.Provider, x.Status });
    }
}
