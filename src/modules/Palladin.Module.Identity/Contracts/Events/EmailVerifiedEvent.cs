using Palladin.Core.Events;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Identity.Contracts.Events;

[PublicAPI]
public sealed record EmailVerifiedEvent(
    Guid UserId,
    Guid OrganizationId,
    Instant VerifiedAt) : IIntegrationEvent;
