using Palladin.Core.Events;
using Palladin.Core.Security;
using Palladin.Module.Identity.Contracts.Events;
using NodaTime;

namespace Palladin.Module.Identity.Domain;

internal sealed class OrganizationMember : EventEntityBase
{
    public Guid OrganizationId { get; private set; }
    public Guid UserId { get; private set; }
    public bool IsOwner { get; private set; }
    public OrganizationMemberStatus Status { get; private set; }
    public Guid? RemovalRequestId { get; private set; }
    public Guid? RemovalRequestedBy { get; private set; }
    public Instant? RemovalRequestedAt { get; private set; }
    public Instant JoinedAt { get; private set; }
    public Instant UpdatedAt { get; private set; }

    public Organization Organization { get; private set; } = null!;
    public User User { get; private set; } = null!;
    public ICollection<OrganizationMemberRole> RoleAssignments { get; private set; } = [];

    private OrganizationMember() { }

    internal static OrganizationMember CreateOwner(
        Guid organizationId,
        Guid userId,
        Role administratorRole,
        Instant now) =>
        new()
        {
            OrganizationId = organizationId,
            UserId = userId,
            IsOwner = true,
            Status = OrganizationMemberStatus.Active,
            JoinedAt = now,
            UpdatedAt = now,
            RoleAssignments = [OrganizationMemberRole.Create(organizationId, userId, administratorRole)],
        };

    internal static OrganizationMember Create(
        Guid organizationId,
        Guid userId,
        Role initialRole,
        string displayName,
        string email,
        Instant now)
    {
        var member = new OrganizationMember
        {
            OrganizationId = organizationId,
            UserId = userId,
            Status = OrganizationMemberStatus.Active,
            JoinedAt = now,
            UpdatedAt = now,
            RoleAssignments = [OrganizationMemberRole.Create(organizationId, userId, initialRole)],
        };

        member.AddEvent(new OrganizationMemberJoinedEvent(
            organizationId, userId, displayName, email, initialRole.Name, now));

        return member;
    }

    internal Permission EffectivePermissions() =>
        RoleAssignments.Aggregate(Permission.None, (permissions, assignment) =>
            permissions | assignment.Role.Permissions);

    internal void ReplaceRoles(
        IReadOnlyCollection<Role> roles,
        string userDisplayName,
        Guid changedBy,
        string changedByName,
        Instant now)
    {
        if (IsOwner)
        {
            return;
        }

        var oldRoleNames = RoleAssignments.Select(assignment => assignment.Role.Name).Order().ToArray();
        var newRoleNames = roles.Select(role => role.Name).Order().ToArray();
        if (oldRoleNames.SequenceEqual(newRoleNames, StringComparer.Ordinal))
        {
            return;
        }

        var requestedRoleIds = roles.Select(role => role.Id).ToHashSet();
        foreach (var assignment in RoleAssignments.Where(assignment => !requestedRoleIds.Contains(assignment.RoleId)).ToList())
        {
            RoleAssignments.Remove(assignment);
        }

        var existingRoleIds = RoleAssignments.Select(assignment => assignment.RoleId).ToHashSet();
        foreach (var role in roles.Where(role => !existingRoleIds.Contains(role.Id)))
        {
            RoleAssignments.Add(OrganizationMemberRole.Create(OrganizationId, UserId, role));
        }

        UpdatedAt = now;
        AddEvent(new OrganizationMemberRoleChangedEvent(
            OrganizationId, UserId, userDisplayName, changedBy, changedByName,
            oldRoleNames, newRoleNames, now));
    }

    internal void RequestRemoval(Guid requestId, Guid requestedBy, Instant now)
    {
        if (IsOwner)
        {
            throw new InvalidOperationException("The organization owner cannot be removed.");
        }

        if (Status == OrganizationMemberStatus.Removing)
        {
            AddOrReplaceEvent(new OrganizationMemberRemovalRequestedEvent(
                RemovalRequestId!.Value,
                OrganizationId,
                UserId,
                RemovalRequestedBy!.Value,
                RemovalRequestedAt!.Value));
            return;
        }

        Status = OrganizationMemberStatus.Removing;
        RemovalRequestId = requestId;
        RemovalRequestedBy = requestedBy;
        RemovalRequestedAt = now;
        UpdatedAt = now;
        AddEvent(new OrganizationMemberRemovalRequestedEvent(
            requestId, OrganizationId, UserId, requestedBy, now));
    }

    internal void CompleteRemoval(string userDisplayName, string removedByName, Instant now)
    {
        if (Status != OrganizationMemberStatus.Removing || RemovalRequestedBy is null)
        {
            throw new InvalidOperationException("Organization Member removal was not requested.");
        }

        AddEvent(new OrganizationMemberRemovedEvent(
            OrganizationId, UserId, userDisplayName, RemovalRequestedBy.Value, removedByName, now));
    }
}
