using System.Text.Json;
using Palladin.Module.Notification.Domain;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Palladin.Module.Notification.Infrastructure.Persistence.Configurations;

[UsedImplicitly]
internal sealed class InboxItemConfiguration : IEntityTypeConfiguration<InboxItem>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly ValueComparer<IReadOnlyDictionary<string, string>> MetadataComparer = new(
        (a, b) => JsonSerializer.Serialize(a, JsonOptions) == JsonSerializer.Serialize(b, JsonOptions),
        v => JsonSerializer.Serialize(v, JsonOptions).GetHashCode(),
        v => v);

    public void Configure(EntityTypeBuilder<InboxItem> builder)
    {
        builder.HasKey(x => new { x.OrganizationId, x.UserId, x.Id });

        builder.Property(x => x.TitleKey).HasMaxLength(200);
        builder.Property(x => x.ScopeType).HasMaxLength(64);
        builder.Property(x => x.Collapsible).HasDefaultValue(false);

        builder.Property(x => x.Metadata)
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, JsonOptions),
                v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, JsonOptions)
                     ?? new Dictionary<string, string>(),
                MetadataComparer);

        builder.HasIndex(x => new { x.OrganizationId, x.UserId, x.OccurredAt, x.Id })
            .IsDescending(false, false, true, true);

        builder.HasIndex(["OrganizationId", "UserId"], "IX_InboxItems_OrganizationId_UserId_Unread")
            .HasFilter("\"ReadAt\" IS NULL");

        builder.HasIndex(x => new { x.OrganizationId, x.SubjectId });

        builder.HasIndex(x => new { x.OrganizationId, x.Type, x.SubjectId });

        builder.HasIndex(x => new { x.OrganizationId, x.UserId, x.Type, x.SubjectId })
            .IsUnique();

        builder.HasIndex(x => new { x.OrganizationId, x.UserId, x.ScopeType, x.ScopeItemId });

        builder.HasIndex(["OrganizationId", "UserId"], "IX_InboxItems_OrganizationId_UserId_Permission")
            .HasFilter("\"RequiredPermission\" IS NOT NULL");
    }
}
