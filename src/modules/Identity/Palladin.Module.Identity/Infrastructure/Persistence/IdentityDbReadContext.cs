using Palladin.Core.Persistence;
using Palladin.Module.Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Identity.Infrastructure.Persistence;

internal sealed class IdentityDbReadContext(DbContextOptions<IdentityDbReadContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<OrganizationMember> OrganizationMembers => Set<OrganizationMember>();
    public DbSet<OrganizationMemberDirectoryEntry> OrganizationMemberDirectoryEntries => Set<OrganizationMemberDirectoryEntry>();
    public DbSet<OrganizationMemberRole> OrganizationMemberRoles => Set<OrganizationMemberRole>();
    public DbSet<OrganizationInvitation> OrganizationInvitations => Set<OrganizationInvitation>();
    public DbSet<OAuthConnection> OAuthConnections => Set<OAuthConnection>();
    public DbSet<SharedUnlockLink> SharedUnlockLinks => Set<SharedUnlockLink>();
    public DbSet<SharedUnlockAuthorization> SharedUnlockAuthorizations => Set<SharedUnlockAuthorization>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<WaitlistEntry> WaitlistEntries => Set<WaitlistEntry>();
    public DbSet<PasswordCredential> PasswordCredentials => Set<PasswordCredential>();
    public DbSet<TotpCredential> TotpCredentials => Set<TotpCredential>();
    public DbSet<TotpRecoveryCode> TotpRecoveryCodes => Set<TotpRecoveryCode>();
    public DbSet<VerificationToken> VerificationTokens => Set<VerificationToken>();
    public DbSet<LoginLockout> LoginLockouts => Set<LoginLockout>();
    public DbSet<LoginRateLimitBucket> LoginRateLimitBuckets => Set<LoginRateLimitBucket>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(IdentityDbWriteContext).Assembly);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        throw new ReadOnlyContextSaveChangesException(nameof(IdentityDbReadContext));
}
