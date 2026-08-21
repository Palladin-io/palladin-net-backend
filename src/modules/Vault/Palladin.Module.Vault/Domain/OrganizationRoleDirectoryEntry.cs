using NodaTime;
using Palladin.Core.Security;

namespace Palladin.Module.Vault.Domain;

internal sealed class OrganizationRoleDirectoryEntry
{
    public Guid OrganizationId { get; private set; }
    public Guid RoleId { get; private set; }
    public bool IsDeleted { get; private set; }
    public bool IsSystem { get; private set; }
    public Permission Permissions { get; private set; }
    public ulong SourceRevision { get; private set; }
    public ulong MutationVersion { get; private set; }
    public Instant UpdatedAt { get; private set; }

    private OrganizationRoleDirectoryEntry() { }

    internal static OrganizationRoleDirectoryEntry Create(
        Guid organizationId,
        Guid roleId,
        ulong sourceRevision,
        bool isSystem,
        Permission permissions,
        Instant updatedAt) =>
        new()
        {
            OrganizationId = organizationId,
            RoleId = roleId,
            IsSystem = isSystem,
            Permissions = permissions,
            SourceRevision = sourceRevision,
            MutationVersion = 1,
            UpdatedAt = updatedAt,
        };

    internal static OrganizationRoleDirectoryEntry CreateDeleted(
        Guid organizationId,
        Guid roleId,
        ulong sourceRevision,
        bool isSystem,
        Permission permissions,
        Instant deletedAt) =>
        new()
        {
            OrganizationId = organizationId,
            RoleId = roleId,
            IsDeleted = true,
            IsSystem = isSystem,
            Permissions = permissions,
            SourceRevision = sourceRevision,
            MutationVersion = 1,
            UpdatedAt = deletedAt,
        };

    internal bool ApplyUpsert(
        ulong sourceRevision,
        bool isSystem,
        Permission permissions,
        Instant updatedAt)
    {
        if (sourceRevision < SourceRevision || sourceRevision == SourceRevision && IsDeleted)
        {
            return false;
        }

        if (sourceRevision == SourceRevision)
        {
            if (IsSystem == isSystem && Permissions == permissions && UpdatedAt == updatedAt)
            {
                return false;
            }

            throw new InvalidOperationException("Conflicting organization role directory revision.");
        }

        IsDeleted = false;
        IsSystem = isSystem;
        Permissions = permissions;
        SourceRevision = sourceRevision;
        UpdatedAt = updatedAt;
        AdvanceMutationVersion();
        return true;
    }

    internal bool ApplyDeletion(
        ulong sourceRevision,
        bool isSystem,
        Permission permissions,
        Instant deletedAt)
    {
        if (sourceRevision < SourceRevision)
        {
            return false;
        }

        if (sourceRevision == SourceRevision)
        {
            if (IsDeleted
                && IsSystem == isSystem
                && Permissions == permissions
                && UpdatedAt == deletedAt)
            {
                return false;
            }

            throw new InvalidOperationException("Conflicting organization role directory revision.");
        }

        IsDeleted = true;
        IsSystem = isSystem;
        Permissions = permissions;
        SourceRevision = sourceRevision;
        UpdatedAt = deletedAt;
        AdvanceMutationVersion();
        return true;
    }

    internal void FencePolicyMutation() => AdvanceMutationVersion();

    private void AdvanceMutationVersion()
    {
        if (MutationVersion == ulong.MaxValue)
        {
            throw new InvalidOperationException("Organization role directory mutation version namespace is exhausted.");
        }

        MutationVersion++;
    }
}
