using Palladin.Core.Events;
using Palladin.Core.Security;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Identity.Contracts.Events;

[PublicAPI]
public sealed record UserUpsertedEvent(
    Guid UserId,
    Guid OrganizationId,
    string DisplayName,
    string Email,
    Permission Permissions,
    Instant UpdatedAt) : IIntegrationEvent;
