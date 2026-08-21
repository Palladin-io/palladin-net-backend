using JetBrains.Annotations;
using NodaTime;
using Palladin.Core.Events;

namespace Palladin.Module.Identity.Contracts.Events;

// Recipient-facing delivery data is carried only to the email consumer. Audit and analytics consumers
// deliberately project no recipient address or token.
[PublicAPI]
public sealed record OrganizationInvitationResentEvent(
    Guid ResendId,
    Guid InvitationId,
    Guid OrganizationId,
    string OrganizationName,
    Guid ResentBy,
    string ResentByName,
    string Email,
    string Language,
    string RoleName,
    string Token,
    int ExpiryHours,
    Instant OccurredAt) : IIntegrationEvent;
