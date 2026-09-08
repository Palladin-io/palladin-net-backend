using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Palladin.Module.Agents.Domain;

namespace Palladin.Module.Agents.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class AgentPairingRequestConfiguration : IEntityTypeConfiguration<AgentPairingRequest>
{
    internal const string PrimaryKey = "PK_agent_pairing_requests";
    internal const string ReservedNameIndex = "ux_agent_pairing_requests_reserved_name";

    public void Configure(EntityTypeBuilder<AgentPairingRequest> builder)
    {
        builder.ToTable("agent_pairing_requests");
        builder.HasKey(x => x.Id).HasName(PrimaryKey);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.OrganizationId).HasColumnName("organization_id");
        builder.Property(x => x.PublicKey).HasColumnName("public_key").HasMaxLength(64);
        builder.Property(x => x.SigningPublicKey).HasColumnName("signing_public_key").HasMaxLength(64);
        builder.Property(x => x.RequestedDisplayName).HasColumnName("requested_display_name").HasMaxLength(64);
        builder.Property(x => x.RequestedType).HasColumnName("requested_type").HasMaxLength(100);
        builder.Property(x => x.Hostname).HasColumnName("hostname").HasMaxLength(253);
        builder.Property(x => x.Ip).HasColumnName("ip").HasMaxLength(45);
        builder.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(64);
        builder.Property(x => x.Type).HasColumnName("type").HasMaxLength(100);
        builder.Property(x => x.ReservedDisplayName).HasColumnName("reserved_display_name").HasMaxLength(64);
        builder.Property(x => x.ReservedDisplayNameKey).HasColumnName("reserved_display_name_key").HasMaxLength(64);
        builder.Property(x => x.Status).HasColumnName("status");
        builder.Property(x => x.AgentId).HasColumnName("agent_id");
        builder.Property(x => x.ApiKeyId).HasColumnName("api_key_id");
        builder.Property(x => x.CredentialSuite).HasColumnName("credential_suite").HasMaxLength(80);
        builder.Property(x => x.CredentialEphemeralPublicKey).HasColumnName("credential_ephemeral_public_key").HasMaxLength(64);
        builder.Property(x => x.CredentialNonce).HasColumnName("credential_nonce").HasMaxLength(48);
        builder.Property(x => x.CredentialCiphertext).HasColumnName("credential_ciphertext").HasColumnType("text");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.ExpiresAt).HasColumnName("expires_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        builder.Property(x => x.Revision)
            .HasColumnName("revision")
            .HasDefaultValue(1u)
            .IsConcurrencyToken();

        builder.HasIndex(x => x.PublicKey);
        builder.HasIndex(x => x.ExpiresAt);
        builder.HasIndex(x => new { x.Status, x.UpdatedAt, x.Id });
        builder.HasIndex(x => new { x.OrganizationId, x.ReservedDisplayNameKey })
            .IsUnique()
            .HasDatabaseName(ReservedNameIndex)
            .HasFilter("status = 1 AND reserved_display_name_key IS NOT NULL");
    }
}
