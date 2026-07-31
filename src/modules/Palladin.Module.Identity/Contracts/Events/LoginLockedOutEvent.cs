using Palladin.Core.Events;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Identity.Contracts.Events;

// EmailHash (not the email) is the analytics distinct id — a lockout can happen for an unknown email
// and the address itself must stay out of analytics/logs.
[PublicAPI]
public sealed record LoginLockedOutEvent(
    string EmailHash,
    Instant LockedUntil,
    Instant OccurredAt) : IIntegrationEvent;
