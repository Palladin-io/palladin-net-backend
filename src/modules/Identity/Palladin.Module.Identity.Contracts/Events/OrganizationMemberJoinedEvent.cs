using Palladin.Core.Events;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Identity.Contracts.Events;

[PublicAPI]
public sealed record OrganizationMemberJoinedEvent(
    Guid OrganizationId,
    Guid UserId,
    string DisplayName,
    string Email,
    string InitialRoleName,
    Instant OccurredAt) : IIntegrationEvent;
