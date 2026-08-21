using JetBrains.Annotations;
using NodaTime;
using Palladin.Core.Events;

namespace Palladin.Module.Identity.Contracts.Events;

[PublicAPI]
public sealed record OrganizationInvitationRoleChangedEvent(
    Guid InvitationId,
    Guid OrganizationId,
    Guid ChangedBy,
    string ChangedByName,
    string PreviousRoleName,
    string RoleName,
    Instant OccurredAt) : IIntegrationEvent;
