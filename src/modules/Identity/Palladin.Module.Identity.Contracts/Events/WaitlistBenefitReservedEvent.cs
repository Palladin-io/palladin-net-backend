using JetBrains.Annotations;
using NodaTime;
using Palladin.Core.Events;

namespace Palladin.Module.Identity.Contracts.Events;

[PublicAPI]
public sealed record WaitlistBenefitReservedEvent(
    Guid EntryId,
    Guid UserId,
    string Plan,
    Instant StartsAt,
    Instant EndsAt,
    Instant OccurredAt) : IIntegrationEvent;
