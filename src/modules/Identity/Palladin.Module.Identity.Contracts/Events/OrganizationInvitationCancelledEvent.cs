using JetBrains.Annotations;
using NodaTime;
using Palladin.Core.Events;

namespace Palladin.Module.Identity.Contracts.Events;

[PublicAPI]
public sealed record OrganizationInvitationCancelledEvent(
    Guid InvitationId,
    Guid OrganizationId,
    Guid CancelledBy,
    string CancelledByName,
    string RoleName,
    Instant OccurredAt) : IIntegrationEvent;
