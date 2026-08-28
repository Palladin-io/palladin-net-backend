using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class ScriptExecutionGrantConfiguration : IEntityTypeConfiguration<ScriptExecutionGrant>
{
    public void Configure(EntityTypeBuilder<ScriptExecutionGrant> builder)
    {
        builder.HasIndex(x => new
        { x.OrganizationId, x.VaultId, x.ScriptEntryId, x.AgentId, x.AgentAccessEpoch, x.Status })
            .IsUnique()
            .HasFilter("\"GrantType\" = 'ScriptExecution' AND \"Status\" = 2");
        builder.HasIndex(x => new
            { x.OrganizationId, x.VaultId, x.ScriptEntryId, x.AgentId, x.AgentAccessEpoch, x.Status },
            "IX_Grants_ScriptExecution_Pending")
            .IsUnique()
            .HasFilter("\"GrantType\" = 'ScriptExecution' AND \"Status\" = 1");
    }
}
