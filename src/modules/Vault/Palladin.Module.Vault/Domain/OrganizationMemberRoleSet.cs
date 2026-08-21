using NodaTime;

namespace Palladin.Module.Vault.Domain;

internal sealed class OrganizationMemberRoleSet
{
    public Guid OrganizationId { get; private set; }
    public Guid UserId { get; private set; }
    public Guid[] RoleIds { get; private set; } = [];
    public ulong Revision { get; private set; }
    public uint AuthorizationVersion { get; private set; }
    public bool IsActive { get; private set; }
    public Instant UpdatedAt { get; private set; }

    private OrganizationMemberRoleSet() { }

    internal static OrganizationMemberRoleSet Create(
        Guid organizationId,
        Guid userId,
        IReadOnlyCollection<Guid> roleIds,
        ulong revision,
        uint authorizationVersion,
        bool isActive,
        Instant updatedAt) =>
        new()
        {
            OrganizationId = organizationId,
            UserId = userId,
            RoleIds = Normalize(roleIds),
            Revision = revision,
            AuthorizationVersion = authorizationVersion,
            IsActive = isActive,
            UpdatedAt = updatedAt,
        };

    internal bool Apply(
        IReadOnlyCollection<Guid> roleIds,
        ulong revision,
        uint authorizationVersion,
        bool isActive,
        Instant updatedAt)
    {
        var normalizedRoleIds = Normalize(roleIds);
        if (revision < Revision || revision == Revision && !IsActive && isActive)
        {
            return false;
        }

        if (revision == Revision)
        {
            if (RoleIds.SequenceEqual(normalizedRoleIds)
                && AuthorizationVersion == authorizationVersion
                && IsActive == isActive
                && UpdatedAt == updatedAt)
            {
                return false;
            }

            throw new InvalidOperationException("Conflicting organization Member role-set replica revision.");
        }

        RoleIds = normalizedRoleIds;
        Revision = revision;
        AuthorizationVersion = authorizationVersion;
        IsActive = isActive;
        UpdatedAt = updatedAt;
        return true;
    }

    private static Guid[] Normalize(IEnumerable<Guid> roleIds) =>
        roleIds.Distinct().Order().ToArray();
}
