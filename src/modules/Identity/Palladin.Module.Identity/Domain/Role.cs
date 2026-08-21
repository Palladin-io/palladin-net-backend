using Palladin.Core.Security;
using Palladin.Core.Events;
using Palladin.Core.Types;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Identity.Contracts.Events;
using NodaTime;

namespace Palladin.Module.Identity.Domain;

internal sealed class Role : EventEntityBase
{
    internal const string AdministratorName = "Administrator";
    internal const string DefaultUserName = "User";
    internal const Permission DefaultUserPermissions = Permission.VaultCreate | Permission.VaultManage;

    public Guid OrganizationId { get; private set; }
    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string NormalizedName { get; private set; } = string.Empty;
    public Permission Permissions { get; private set; }
    public bool IsSystem { get; private set; }
    public ulong VaultAccessRevision { get; private set; }
    public Instant CreatedAt { get; private set; }

    public Organization Organization { get; private set; } = null!;
    public ICollection<OrganizationMemberRole> MemberAssignments { get; private set; } = [];

    private Role() { }

    internal static Role Create(
        Guid id,
        Guid organizationId,
        string name,
        Permission permissions,
        bool isSystem,
        Instant now)
    {
        var normalizedName = NormalizeName(name);
        if (id == Guid.Empty || organizationId == Guid.Empty || normalizedName.Length is 0 or > 100)
        {
            throw new DomainException("Organization role identity or name is invalid.");
        }

        if (!isSystem && !OrganizationRolePermissions.IsAssignable(permissions))
        {
            throw new DomainException("Organization role permissions contain unsupported flags.");
        }

        var role = new Role
        {
            Id = id,
            OrganizationId = organizationId,
            Name = name.Trim(),
            NormalizedName = normalizedName,
            Permissions = permissions,
            IsSystem = isSystem,
            VaultAccessRevision = 1,
            CreatedAt = now,
        };

        role.AddOrReplaceEvent(new OrganizationRoleUpsertedEvent(
            organizationId,
            id,
            role.VaultAccessRevision,
            EntityChange.Created,
            role.IsSystem,
            role.Permissions,
            now));
        return role;
    }

    internal static Role CreateAdministrator(Guid id, Guid organizationId, Instant now) =>
        Create(id, organizationId, AdministratorName, (Permission)int.MaxValue, isSystem: true, now);

    internal static Role CreateDefaultUser(Guid id, Guid organizationId, Instant now) =>
        Create(id, organizationId, DefaultUserName, DefaultUserPermissions, isSystem: true, now);

    internal bool IsAdministrator => IsSystem && NormalizedName == NormalizeName(AdministratorName);

    internal bool IsDefaultUser => IsSystem && NormalizedName == NormalizeName(DefaultUserName);

    internal bool UpdateCustom(string name, Permission permissions, Instant now)
    {
        if (IsSystem)
        {
            throw new DomainException("System organization roles are immutable.");
        }

        var normalizedName = NormalizeName(name);
        if (normalizedName.Length is 0 or > 100 || !OrganizationRolePermissions.IsAssignable(permissions))
        {
            throw new DomainException("Organization role name or permissions are invalid.");
        }

        var trimmedName = name.Trim();
        if (Name == trimmedName && Permissions == permissions)
        {
            return false;
        }

        Name = trimmedName;
        NormalizedName = normalizedName;
        Permissions = permissions;
        AdvanceVaultAccessRevision();
        AddOrReplaceEvent(new OrganizationRoleUpsertedEvent(
            OrganizationId,
            Id,
            VaultAccessRevision,
            EntityChange.Updated,
            IsSystem,
            Permissions,
            now));
        return true;
    }

    internal void MarkDeleted(Instant now)
    {
        AdvanceVaultAccessRevision();
        AddEvent(new OrganizationRoleDeletedEvent(
            OrganizationId,
            Id,
            VaultAccessRevision,
            IsSystem,
            Permissions,
            now));
    }

    private void AdvanceVaultAccessRevision()
    {
        if (VaultAccessRevision == ulong.MaxValue)
        {
            throw new InvalidOperationException("Organization role Vault-access revision namespace is exhausted.");
        }

        VaultAccessRevision++;
    }

    internal static string NormalizeName(string name) => name.Trim().ToUpperInvariant();
}
