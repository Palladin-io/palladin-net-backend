using JetBrains.Annotations;
using NodaTime;
using Palladin.Core.Events;

namespace Palladin.Module.Identity.Contracts.Events;

[PublicAPI]
public sealed record WaitlistDeveloperBenefitActivatedEvent(
    Guid UserId,
    Guid OrganizationId,
    string Email,
    string Language,
    Instant StartsAt,
    Instant EndsAt,
    Instant UpdatedAt) : IIntegrationEvent;
