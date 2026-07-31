using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;

namespace Palladin.Module.Identity.Triggers;

internal static class OrganizationMembershipAuditCommand
{
    internal static AppendAuditLogCommand Create(
        Guid organizationId,
        string eventType,
        NodaTime.Instant occurredAt,
        Guid actorId,
        string actorName,
        IReadOnlyDictionary<string, string> metadata) =>
        new(
            organizationId,
            eventType,
            AuditActorType.User,
            AuditResult.Succeeded,
            occurredAt,
            actorId,
            null,
            null,
            null,
            actorName,
            null,
            null,
            metadata);
}
