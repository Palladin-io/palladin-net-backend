using Palladin.Core.Events;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Identity.Contracts.Events;

[PublicAPI]
public sealed record UserSignedUpEvent(
    Guid UserId,
    Guid OrganizationId,
    string DisplayName,
    string Provider,
    string Platform,
    Instant CreatedAt) : IIntegrationEvent;
