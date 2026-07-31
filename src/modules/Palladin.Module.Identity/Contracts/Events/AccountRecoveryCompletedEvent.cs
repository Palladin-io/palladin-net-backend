using Palladin.Core.Events;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Identity.Contracts.Events;

[PublicAPI]
public sealed record AccountRecoveryCompletedEvent(
    Guid UserId,
    Guid OrganizationId,
    string DisplayName,
    Instant UpdatedAt) : IIntegrationEvent;
