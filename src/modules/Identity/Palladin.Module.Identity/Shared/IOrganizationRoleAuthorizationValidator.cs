using JetBrains.Annotations;
using Palladin.Core.Security;

namespace Palladin.Module.Identity.Shared;

[PublicAPI]
public sealed record OrganizationRoleAuthorizationSnapshot(
    ulong Revision,
    Permission Permissions,
    Permission ActorPermissions,
    bool ActorIsOwner);

[PublicAPI]
public interface IOrganizationRoleAuthorizationValidator
{
    Task<OrganizationRoleAuthorizationSnapshot?> GetAssignableSnapshotAsync(
        Guid organizationId,
        Guid roleId,
        Guid actorUserId,
        CancellationToken cancellationToken = default);
}
