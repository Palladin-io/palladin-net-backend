using JetBrains.Annotations;
using NodaTime;
using Palladin.Core.Events;

namespace Palladin.Module.Identity.Contracts.Events;

[PublicAPI]
public sealed record OrganizationMemberRolesUpsertedEvent(
    Guid OrganizationId,
    Guid UserId,
    IReadOnlyList<Guid> RoleIds,
    ulong Revision,
    uint AuthorizationVersion,
    bool IsActive,
    Instant UpdatedAt) : IIntegrationEvent;
