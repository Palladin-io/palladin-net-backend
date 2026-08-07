using Palladin.Core.Events;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Identity.Contracts.Events;

// Token travels in plaintext only here (broker-internal) — the email trigger needs it to build the
// verification link. It must never be logged; the database stores only its hash.
[PublicAPI]
public sealed record WaitlistJoinedEvent(
    Guid EntryId,
    string Email,
    string Language,
    string Token,
    int ExpiryHours,
    Instant OccurredAt) : IIntegrationEvent;
