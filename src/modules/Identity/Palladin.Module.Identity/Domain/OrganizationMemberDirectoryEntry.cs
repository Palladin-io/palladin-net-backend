using NodaTime;

namespace Palladin.Module.Identity.Domain;

internal sealed class OrganizationMemberDirectoryEntry
{
    public Guid OrganizationId { get; private set; }
    public Guid UserId { get; private set; }
    public string DisplayName { get; private set; } = string.Empty;
    public Instant UpdatedAt { get; private set; }

    private OrganizationMemberDirectoryEntry() { }

    internal static OrganizationMemberDirectoryEntry Create(
        Guid organizationId,
        Guid userId,
        string displayName,
        Instant now) => new()
        {
            OrganizationId = organizationId,
            UserId = userId,
            DisplayName = displayName,
            UpdatedAt = now,
        };

    internal void Refresh(string displayName, Instant now)
    {
        DisplayName = displayName;
        UpdatedAt = now;
    }
}
