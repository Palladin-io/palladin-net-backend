using Microsoft.EntityFrameworkCore;
using Palladin.Core.Security;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Shared;

namespace Palladin.Module.Identity.Infrastructure.Jwt;

internal sealed class OrganizationRoleAuthorizationValidator(IdentityDbReadContext readContext)
    : IOrganizationRoleAuthorizationValidator
{
    public async Task<OrganizationRoleAuthorizationSnapshot?> GetAssignableSnapshotAsync(
        Guid organizationId,
        Guid roleId,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var actor = await readContext.OrganizationMembers
            .Include(x => x.RoleAssignments)
                .ThenInclude(x => x.Role)
            .SingleOrDefaultAsync(
                x => x.OrganizationId == organizationId
                     && x.UserId == actorUserId
                     && x.Status == OrganizationMemberStatus.Active,
                cancellationToken);
        var role = await readContext.Roles
            .Where(x => x.OrganizationId == organizationId && x.Id == roleId)
            .SingleOrDefaultAsync(cancellationToken);
        if (actor is null || role is null)
        {
            return null;
        }

        var actorPermissions = actor.EffectivePermissions();
        var canAssign = role.IsAdministrator
            ? actor.IsOwner
            : (!role.IsSystem || role.IsDefaultUser)
              && (actor.IsOwner || (role.Permissions & actorPermissions) == role.Permissions);
        if (!canAssign)
        {
            return null;
        }

        return new OrganizationRoleAuthorizationSnapshot(
            role.VaultAccessRevision,
            role.Permissions,
            actorPermissions,
            actor.IsOwner);
    }
}
