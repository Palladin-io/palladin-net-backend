using Palladin.Core.Events;
using Palladin.Module.Identity.Contracts.Events;
using NodaTime;

namespace Palladin.Module.Identity.Domain;

internal sealed class OrganizationInvitation : EventEntityBase
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid? RoleId { get; private set; }
    public string RoleName { get; private set; } = string.Empty;
    public Guid InvitedBy { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public string TokenHash { get; private set; } = string.Empty;
    public Instant ExpiresAt { get; private set; }
    public Instant LastSentAt { get; private set; }
    public Instant? AcceptedAt { get; private set; }
    public Instant? CancelledAt { get; private set; }
    public Instant CreatedAt { get; private set; }

    public Organization Organization { get; private set; } = null!;
    public Role? Role { get; private set; }

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
            RoleName = roleName,
            InvitedBy = invitedBy,
            Email = email,
            TokenHash = tokenHash,
            ExpiresAt = now + ttl,
            LastSentAt = now,
            CreatedAt = now,
        };

        invitation.AddEvent(new OrganizationMemberInvitedEvent(
            id, organizationId, organizationName, invitedBy, invitedByName, email, language,
            roleName, token, (int)ttl.TotalHours, now));

        return invitation;
    }

    internal bool IsPending(Instant now) =>
        AcceptedAt is null && CancelledAt is null && now < ExpiresAt;

    internal bool CanAccept(Instant now) => IsPending(now);

    internal void Accept(Instant now) => AcceptedAt = now;

    internal void Cancel(Guid cancelledBy, string cancelledByName, Instant now)
    {
        if (!IsPending(now))
        {
            throw new InvalidOperationException("Only a pending organization invitation can be cancelled.");
        }

        CancelledAt = now;
        AddEvent(new OrganizationInvitationCancelledEvent(
            Id,
            OrganizationId,
            cancelledBy,
            cancelledByName,
            RoleName,
            now));
    }

    internal void Resend(
        Guid resendId,
        string organizationName,
        Guid resentBy,
        string resentByName,
        string language,
        string token,
        string tokenHash,
        Duration ttl,
        Instant now)
    {
        if (!IsPending(now))
        {
            throw new InvalidOperationException("Only a pending organization invitation can be resent.");
        }

        InvitedBy = resentBy;
        TokenHash = tokenHash;
        LastSentAt = now;
        ExpiresAt = now + ttl;
        AddEvent(new OrganizationInvitationResentEvent(
            resendId,
            Id,
            OrganizationId,
            organizationName,
            resentBy,
            resentByName,
            Email,
            language,
            RoleName,
            token,
            (int)ttl.TotalHours,
            now));
    }

    internal bool ChangeRole(
        Guid roleId,
        string roleName,
        Guid changedBy,
        string changedByName,
        Instant now)
    {
        if (!IsPending(now))
        {
            throw new InvalidOperationException("Only a pending organization invitation can change role.");
        }

        if (RoleId == roleId)
        {
            return false;
        }

        var previousRoleName = RoleName;
        RoleId = roleId;
        RoleName = roleName;
        AddEvent(new OrganizationInvitationRoleChangedEvent(
            Id,
            OrganizationId,
            changedBy,
            changedByName,
            previousRoleName,
            roleName,
            now));
        return true;
    }

    internal void DetachHistoricalRole(string roleName, Instant now)
    {
        if (CanAccept(now))
        {
            throw new InvalidOperationException("An active organization invitation must retain its role.");
        }

        RoleName = roleName;
        RoleId = null;
        Role = null;
    }
}
