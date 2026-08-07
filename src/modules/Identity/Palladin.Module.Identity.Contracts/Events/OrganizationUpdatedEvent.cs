using Palladin.Core.Events;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Identity.Contracts.Events;

[PublicAPI]
public sealed record OrganizationUpdatedEvent(
    Guid OrganizationId,
    string Name,
    Guid UpdatedBy,
    string UpdatedByName,
    string[] FieldsChanged,
    Instant UpdatedAt) : IIntegrationEvent;
