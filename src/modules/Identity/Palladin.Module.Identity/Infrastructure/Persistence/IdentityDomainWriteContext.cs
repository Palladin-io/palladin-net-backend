using Palladin.Core.Events;
using Palladin.Core.Persistence;
using Palladin.Core.Types;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Identity.Infrastructure.Persistence;

internal sealed class IdentityDomainWriteContext(
    IdentityDbWriteContext writeContext,
    IEnumerable<IEventPublisher> eventPublishers)
    : DomainWriteContextBase(writeContext, eventPublishers)
{
    public IQueryable<User> Users => Track<User>();
    public IQueryable<Organization> Organizations => Track<Organization>();
    public IQueryable<Role> Roles => Track<Role>();
    public IQueryable<OrganizationMember> OrganizationMembers => Track<OrganizationMember>();
    public IQueryable<OrganizationMemberRole> OrganizationMemberRoles => Track<OrganizationMemberRole>();
    public IQueryable<OrganizationInvitation> OrganizationInvitations => Track<OrganizationInvitation>();
    public IQueryable<OAuthConnection> OAuthConnections => Track<OAuthConnection>();
    public IQueryable<RefreshToken> RefreshTokens => Track<RefreshToken>();
    public IQueryable<WaitlistEntry> WaitlistEntries => Track<WaitlistEntry>();
    public IQueryable<PasswordCredential> PasswordCredentials => Track<PasswordCredential>();
    public IQueryable<TotpCredential> TotpCredentials => Track<TotpCredential>();
    public IQueryable<TotpRecoveryCode> TotpRecoveryCodes => Track<TotpRecoveryCode>();
    public IQueryable<VerificationToken> VerificationTokens => Track<VerificationToken>();
    public IQueryable<LoginLockout> LoginLockouts => Track<LoginLockout>();
    public IQueryable<OrganizationRoleVaultAccessDispatch> OrganizationRoleVaultAccessDispatches =>
        Track<OrganizationRoleVaultAccessDispatch>();
    public IQueryable<OrganizationMemberRoleSetDispatch> OrganizationMemberRoleSetDispatches =>
        Track<OrganizationMemberRoleSetDispatch>();

    protected override async Task PrepareEventsAsync(
        IReadOnlyCollection<IEvent> events,
        CancellationToken cancellationToken = default)
    {
        foreach (var @event in events)
        {
            switch (@event)
            {
                case OrganizationRoleUpsertedEvent roleUpserted:
                    await PrepareRoleDispatchAsync(
                        roleUpserted.OrganizationId,
                        roleUpserted.RoleId,
                        roleUpserted.Revision,
                        roleUpserted.Change,
                        isDeleted: false,
                        roleUpserted.IsSystem,
                        roleUpserted.Permissions,
                        roleUpserted.UpdatedAt,
                        cancellationToken);
                    break;
                case OrganizationRoleDeletedEvent roleDeleted:
                    await PrepareRoleDispatchAsync(
                        roleDeleted.OrganizationId,
                        roleDeleted.RoleId,
                        roleDeleted.Revision,
                        EntityChange.Updated,
                        isDeleted: true,
                        roleDeleted.IsSystem,
                        roleDeleted.Permissions,
                        roleDeleted.DeletedAt,
                        cancellationToken);
                    break;
                case OrganizationMemberRolesUpsertedEvent memberRoles:
                    var memberDispatch = Tracked<OrganizationMemberRoleSetDispatch>().FirstOrDefault(
                        x => x.OrganizationId == memberRoles.OrganizationId
                             && x.UserId == memberRoles.UserId)
                        ?? await OrganizationMemberRoleSetDispatches.FirstOrDefaultAsync(
                            x => x.OrganizationId == memberRoles.OrganizationId
                                 && x.UserId == memberRoles.UserId,
                            cancellationToken);
                    if (memberDispatch is null)
                    {
                        Add(OrganizationMemberRoleSetDispatch.Create(
                            memberRoles.OrganizationId,
                            memberRoles.UserId,
                            memberRoles.RoleIds,
                            memberRoles.Revision,
                            memberRoles.AuthorizationVersion,
                            memberRoles.IsActive,
                            memberRoles.UpdatedAt));
                    }
                    else
                    {
                        memberDispatch.Apply(
                            memberRoles.RoleIds,
                            memberRoles.Revision,
                            memberRoles.AuthorizationVersion,
                            memberRoles.IsActive,
                            memberRoles.UpdatedAt);
                    }

                    break;
            }
        }
    }

    protected override Task HandleEventAsync(
        IEvent @event,
        CancellationToken cancellationToken = default) =>
        @event is OrganizationRoleUpsertedEvent
            or OrganizationRoleDeletedEvent
            or OrganizationMemberRolesUpsertedEvent
            ? Task.CompletedTask
            : base.HandleEventAsync(@event, cancellationToken);

    private async Task PrepareRoleDispatchAsync(
        Guid organizationId,
        Guid roleId,
        ulong revision,
        EntityChange change,
        bool isDeleted,
        bool isSystem,
        Palladin.Core.Security.Permission permissions,
        NodaTime.Instant occurredAt,
        CancellationToken cancellationToken)
    {
        var dispatch = Tracked<OrganizationRoleVaultAccessDispatch>().FirstOrDefault(
            x => x.OrganizationId == organizationId && x.RoleId == roleId)
            ?? await OrganizationRoleVaultAccessDispatches.FirstOrDefaultAsync(
                x => x.OrganizationId == organizationId && x.RoleId == roleId,
                cancellationToken);
        if (dispatch is null)
        {
            Add(OrganizationRoleVaultAccessDispatch.Create(
                organizationId,
                roleId,
                revision,
                change,
                isDeleted,
                isSystem,
                permissions,
                occurredAt));
            return;
        }

        dispatch.Apply(revision, change, isDeleted, isSystem, permissions, occurredAt);
    }
}
