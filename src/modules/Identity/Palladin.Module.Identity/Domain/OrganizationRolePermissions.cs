using Palladin.Core.Security;

namespace Palladin.Module.Identity.Domain;

internal static class OrganizationRolePermissions
{
    internal static readonly IReadOnlyList<Permission> Assignable =
    [
        Permission.AddUser,
        Permission.OrganizationManagement,
        Permission.VaultCreate,
        Permission.VaultManage,
        Permission.AgentManage,
        Permission.GrantManage,
        Permission.BillingManage,
        Permission.AuditView,
        Permission.ReadApiKey,
        Permission.WriteApiKey,
    ];

    internal static readonly Permission AssignableMask = Assignable.Aggregate(
        Permission.None,
        (mask, permission) => mask | permission);

    internal static bool IsAssignable(Permission permissions) =>
        (permissions & ~AssignableMask) == Permission.None;

    internal static bool HasGrantManage(Permission permissions) =>
        (permissions & Permission.GrantManage) == Permission.GrantManage;
}
