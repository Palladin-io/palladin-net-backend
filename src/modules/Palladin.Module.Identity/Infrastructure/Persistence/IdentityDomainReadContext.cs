using Palladin.Core.Persistence;
using Palladin.Module.Identity.Domain;

namespace Palladin.Module.Identity.Infrastructure.Persistence;

internal sealed class IdentityDomainReadContext(IdentityDbReadContext readContext) : DomainReadContextBase(readContext)
{
    public IQueryable<User> Users => Query<User>();
    public IQueryable<Organization> Organizations => Query<Organization>();
    public IQueryable<Role> Roles => Query<Role>();
    public IQueryable<OrganizationMember> OrganizationMembers => Query<OrganizationMember>();
    public IQueryable<OrganizationMemberRole> OrganizationMemberRoles => Query<OrganizationMemberRole>();
    public IQueryable<OrganizationInvitation> OrganizationInvitations => Query<OrganizationInvitation>();
    public IQueryable<OAuthConnection> OAuthConnections => Query<OAuthConnection>();
    public IQueryable<RefreshToken> RefreshTokens => Query<RefreshToken>();
    public IQueryable<WaitlistEntry> WaitlistEntries => Query<WaitlistEntry>();
    public IQueryable<PasswordCredential> PasswordCredentials => Query<PasswordCredential>();
    public IQueryable<TotpCredential> TotpCredentials => Query<TotpCredential>();
    public IQueryable<TotpRecoveryCode> TotpRecoveryCodes => Query<TotpRecoveryCode>();
    public IQueryable<VerificationToken> VerificationTokens => Query<VerificationToken>();
    public IQueryable<LoginLockout> LoginLockouts => Query<LoginLockout>();
}
