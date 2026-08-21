using JetBrains.Annotations;
using NodaTime;
using Palladin.Core.Events;
using Palladin.Core.Security;

namespace Palladin.Module.Identity.Contracts.Events;

[PublicAPI]
public sealed record OrganizationRoleDeletedEvent(
    Guid OrganizationId,
    Guid RoleId,
    ulong Revision,
    bool IsSystem,
    Permission Permissions,
    Instant DeletedAt) : IIntegrationEvent;
