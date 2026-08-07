namespace Palladin.Module.Identity.Domain;

internal sealed class OrganizationMemberRole
{
    public Guid OrganizationId { get; private set; }
    public Guid UserId { get; private set; }
    public Guid RoleId { get; private set; }

    public OrganizationMember Member { get; private set; } = null!;
    public Role Role { get; private set; } = null!;

    private OrganizationMemberRole() { }

    internal static OrganizationMemberRole Create(Guid organizationId, Guid userId, Role role) =>
        new()
        {
            OrganizationId = organizationId,
            UserId = userId,
            RoleId = role.Id,
            Role = role,
        };
}
