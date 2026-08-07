using Palladin.Core.Events;
using Palladin.Module.Identity.Contracts.Events;
using NodaTime;

namespace Palladin.Module.Identity.Domain;

internal sealed class OrganizationInvitation : EventEntityBase
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid RoleId { get; private set; }
    public Guid InvitedBy { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public string TokenHash { get; private set; } = string.Empty;
    public Instant ExpiresAt { get; private set; }
    public Instant? AcceptedAt { get; private set; }
    public Instant CreatedAt { get; private set; }

    public Organization Organization { get; private set; } = null!;
    public Role Role { get; private set; } = null!;

    private OrganizationInvitation() { }

    internal static OrganizationInvitation Create(
        Guid id,
        Guid organizationId,
        string organizationName,
        Guid roleId,
        string roleName,
        Guid invitedBy,
        string invitedByName,
        string email,
        string language,
        string token,
        string tokenHash,
        Duration ttl,
        Instant now)
    {
        var invitation = new OrganizationInvitation
        {
            Id = id,
            OrganizationId = organizationId,
            RoleId = roleId,
            InvitedBy = invitedBy,
            Email = email,
            TokenHash = tokenHash,
            ExpiresAt = now + ttl,
            CreatedAt = now,
        };

        invitation.AddEvent(new OrganizationMemberInvitedEvent(
            id, organizationId, organizationName, invitedBy, invitedByName, email, language,
            roleName, token, (int)ttl.TotalHours, now));

        return invitation;
    }

    internal bool CanAccept(Instant now) => AcceptedAt is null && now <= ExpiresAt;

    internal void Accept(Instant now) => AcceptedAt = now;
}
