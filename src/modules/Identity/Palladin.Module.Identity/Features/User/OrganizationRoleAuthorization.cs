using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.Persistence;

namespace Palladin.Module.Identity.Features;

internal static class OrganizationRoleAuthorization
{
    internal static bool ChangesGrantManage(Permission current, Permission proposed) =>
        OrganizationRolePermissions.HasGrantManage(current)
        != OrganizationRolePermissions.HasGrantManage(proposed);

    internal static bool IsSubsetOf(Permission requested, Permission ceiling) =>
        (requested & ~ceiling) == Permission.None;

    internal static bool CanManageCustomRole(
        OrganizationMember actor,
        Permission current,
        Permission proposed) =>
        actor.Status == OrganizationMemberStatus.Active
        && (actor.IsOwner
            || IsSubsetOf(current, actor.EffectivePermissions())
            && IsSubsetOf(proposed, actor.EffectivePermissions()));

    internal static bool CanAssignRole(
        OrganizationMember actor,
        Role role,
        bool allowAdministratorForOwner)
    {
        if (actor.Status != OrganizationMemberStatus.Active)
        {
            return false;
        }

        if (role.IsAdministrator)
        {
            return allowAdministratorForOwner && actor.IsOwner;
        }

        if (role.IsSystem && !role.IsDefaultUser)
        {
            return false;
        }

        return actor.IsOwner || IsSubsetOf(role.Permissions, actor.EffectivePermissions());
    }

    internal static bool CanAssignRoleDuringGrantManageCutover(
        OrganizationMember actor,
        Role role,
        bool allowAdministratorForOwner) =>
        !OrganizationRolePermissions.HasGrantManage(role.Permissions)
        && CanAssignRole(actor, role, allowAdministratorForOwner);

    internal static async Task RevokeRefreshTokensAsync(
        IdentityDomainWriteContext domainWriteContext,
        Guid organizationId,
        IReadOnlyCollection<Guid> userIds,
        Instant now,
        CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
        {
            return;
        }

        var tokens = await domainWriteContext.RefreshTokens
            .Where(token => token.OrganizationId == organizationId
                            && userIds.Contains(token.UserId)
                            && token.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var token in tokens)
        {
            token.Revoke(now);
        }
    }
}
