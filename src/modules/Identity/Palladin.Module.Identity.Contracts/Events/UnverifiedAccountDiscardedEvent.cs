using Palladin.Core.Events;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Identity.Contracts.Events;

// An empty, never-verified password account was replaced by a verified OAuth sign-in for the same e-mail.
// Its personal organization no longer exists; consumers drop every replica of the user and organization.
[PublicAPI]
public sealed record UnverifiedAccountDiscardedEvent(
    Guid UserId,
    Guid OrganizationId,
    Instant OccurredAt) : IIntegrationEvent;
