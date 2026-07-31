using Palladin.Core.Security;
using NodaTime;

namespace Palladin.Module.Identity.Domain;

internal sealed class Role
{
    public Guid OrganizationId { get; private set; }
    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public Permission Permissions { get; private set; }
    public bool IsSystem { get; private set; }
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
        Instant now) =>
        new()
        {
            Id = id,
            OrganizationId = organizationId,
            Name = name,
            Permissions = permissions,
            IsSystem = isSystem,
            CreatedAt = now,
        };

    internal static Role CreateAdministrator(Guid id, Guid organizationId, Instant now) =>
        Create(id, organizationId, "Administrator", (Permission)int.MaxValue, isSystem: true, now);
}
