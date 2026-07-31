using Palladin.Core.Events;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Identity.Contracts.Events;

[PublicAPI]
public sealed record OrganizationCreatedEvent(
    Guid OrganizationId,
    string Name,
    Guid CreatedBy,
    string CreatedByName,
    Instant CreatedAt) : IIntegrationEvent;
