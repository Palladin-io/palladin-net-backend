using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class OrganizationApproverConfiguration : IEntityTypeConfiguration<OrganizationApprover>
{
    public void Configure(EntityTypeBuilder<OrganizationApprover> builder)
    {
        builder.HasKey(x => new { x.OrganizationId, x.UserId });
        builder.Property(x => x.UpdatedAt).IsConcurrencyToken();
    }
}
