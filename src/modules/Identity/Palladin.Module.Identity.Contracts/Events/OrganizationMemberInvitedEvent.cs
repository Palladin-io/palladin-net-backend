using Palladin.Core.Events;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Identity.Contracts.Events;

// Recipient-facing and audit metadata is denormalized so downstream consumers never query Identity.
[PublicAPI]
public sealed record OrganizationMemberInvitedEvent(
    Guid InvitationId,
    Guid OrganizationId,
    string OrganizationName,
    Guid InvitedBy,
    string InvitedByName,
    string Email,
    string Language,
    string RoleName,
    string Token,
    int ExpiryHours,
    Instant OccurredAt) : IIntegrationEvent;
