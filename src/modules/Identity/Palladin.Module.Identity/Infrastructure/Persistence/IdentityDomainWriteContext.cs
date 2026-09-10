using Palladin.Core.Events;
using Palladin.Core.Persistence;
using Palladin.Module.Identity.Domain;

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
    public IQueryable<OrganizationMemberDirectoryEntry> OrganizationMemberDirectoryEntries => Track<OrganizationMemberDirectoryEntry>();
    public IQueryable<OrganizationMemberRole> OrganizationMemberRoles => Track<OrganizationMemberRole>();
    public IQueryable<OrganizationInvitation> OrganizationInvitations => Track<OrganizationInvitation>();
    public IQueryable<OAuthConnection> OAuthConnections => Track<OAuthConnection>();
    public IQueryable<SharedUnlockLink> SharedUnlockLinks => Track<SharedUnlockLink>();
    public IQueryable<SharedUnlockAuthorization> SharedUnlockAuthorizations => Track<SharedUnlockAuthorization>();
    public IQueryable<RefreshToken> RefreshTokens => Track<RefreshToken>();
    public IQueryable<WaitlistEntry> WaitlistEntries => Track<WaitlistEntry>();
    public IQueryable<PasswordCredential> PasswordCredentials => Track<PasswordCredential>();
    public IQueryable<TotpCredential> TotpCredentials => Track<TotpCredential>();
    public IQueryable<TotpRecoveryCode> TotpRecoveryCodes => Track<TotpRecoveryCode>();
    public IQueryable<VerificationToken> VerificationTokens => Track<VerificationToken>();
    public IQueryable<LoginLockout> LoginLockouts => Track<LoginLockout>();
    public IQueryable<LoginRateLimitBucket> LoginRateLimitBuckets => Track<LoginRateLimitBucket>();
}
