using Palladin.Core.Security;
using NodaTime;

namespace Palladin.Module.Notification.Domain;

internal sealed class User
{
    public Guid UserId { get; private set; }
    public Guid OrganizationId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public Permission Permissions { get; private set; }
    public Instant UpdatedAt { get; private set; }

    private User() { }

    internal static User Create(
        Guid userId, Guid organizationId, string name, string email, Permission permissions, Instant updatedAt) =>
        new()
        {
            UserId = userId,
            OrganizationId = organizationId,
            Name = name,
            Email = email,
            Permissions = permissions,
            UpdatedAt = updatedAt,
        };

    internal void Update(string name, string email, Instant updatedAt)
    {
        Name = name;
        Email = email;
        UpdatedAt = updatedAt;
    }

    internal bool ApplyPermissions(Permission permissions)
    {
        if (Permissions == permissions)
        {
            return false;
        }

        Permissions = permissions;
        return true;
    }
}
