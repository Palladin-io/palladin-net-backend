using JetBrains.Annotations;
using NodaTime;
using Palladin.Core.Events;
using Palladin.Core.Security;
using Palladin.Core.Types;

namespace Palladin.Module.Identity.Contracts.Events;

[PublicAPI]
public sealed record OrganizationRoleUpsertedEvent(
    Guid OrganizationId,
    Guid RoleId,
    ulong Revision,
    EntityChange Change,
    bool IsSystem,
    Permission Permissions,
    Instant UpdatedAt) : IIntegrationEvent;
