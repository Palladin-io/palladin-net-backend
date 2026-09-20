using System.Text.Json;
using Palladin.Module.Audit.Domain;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Palladin.Module.Audit.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class AuditLogEntryConfiguration : IEntityTypeConfiguration<AuditLogEntry>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public void Configure(EntityTypeBuilder<AuditLogEntry> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.EventType).HasMaxLength(64);
        builder.Property(x => x.Result).HasConversion<short>();
        builder.Property(x => x.AgentName).HasMaxLength(200);
        builder.Property(x => x.ActorName).HasMaxLength(200);
        builder.Property(x => x.IpAddress).HasMaxLength(45);

        builder.Property(x => x.Metadata)
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, JsonOptions),
                v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, JsonOptions)!
                     ?? new Dictionary<string, string>());

        // Query indexes: global organization-scoped and vault-scoped logs, newest-first
        // via cursor on (CreatedAt, Id).
        builder.HasIndex(x => new { x.OrganizationId, x.CreatedAt, x.Id });
        builder.HasIndex(x => new { x.VaultId, x.CreatedAt, x.Id });

        // Explicit occurrence IDs use the PK: timestamps cannot distinguish simultaneous receipts.
        builder.HasIndex(x => new { x.OrganizationId, x.EventType, x.VaultId, x.AgentId, x.EntryId, x.OccurredAt })
            .IsUnique()
            .HasFilter("NOT \"HasExplicitOccurrenceId\"")
            .AreNullsDistinct(false);
    }
}
