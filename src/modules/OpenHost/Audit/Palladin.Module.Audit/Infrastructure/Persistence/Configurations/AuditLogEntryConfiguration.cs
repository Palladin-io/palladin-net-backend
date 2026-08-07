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

        // Idempotency: a MassTransit redelivery of the same source event must not create a duplicate
        // row. Natural key = (org, event type, resource, source timestamp). Legitimate repeats (e.g.
        // multiple credential.accessed) differ by OccurredAt so they pass. Consumers catch the unique
        // violation and skip. ResourceId is represented here by the relevant id columns; we key on the
        // tuple that uniquely identifies a single source-event occurrence.
        // AreNullsDistinct(false) — PostgreSQL's default NULLS DISTINCT lets two NULL VaultId/AgentId/
        // EntryId rows both INSERT past the pre-check (e.g. agent.enrolled has Vault/Entry NULL); we need
        // NULLS NOT DISTINCT (PG15+) so the safety net actually catches the race.
        builder.HasIndex(x => new { x.OrganizationId, x.EventType, x.VaultId, x.AgentId, x.EntryId, x.OccurredAt })
            .IsUnique()
            .AreNullsDistinct(false);
    }
}
