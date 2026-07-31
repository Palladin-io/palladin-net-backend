using Palladin.Core.Events;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Identity.Contracts.Events;

[PublicAPI]
public sealed record OrganizationMemberRoleChangedEvent(
    Guid OrganizationId,
    Guid UserId,
    string UserDisplayName,
    Guid ChangedBy,
    string ChangedByName,
    IReadOnlyList<string> OldRoles,
    IReadOnlyList<string> NewRoles,
    Instant OccurredAt) : IIntegrationEvent;
