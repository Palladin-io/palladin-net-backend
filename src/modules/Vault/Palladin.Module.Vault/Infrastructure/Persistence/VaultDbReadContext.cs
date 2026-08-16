using Palladin.Core.Persistence;
using Palladin.Module.Vault.Domain;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Vault.Infrastructure.Persistence;

internal sealed class VaultDbReadContext(DbContextOptions<VaultDbReadContext> options) : DbContext(options)
{
    public DbSet<Domain.Vault> Vaults => Set<Domain.Vault>();
    public DbSet<VaultMember> VaultMembers => Set<VaultMember>();
    public DbSet<VaultMemberKeyEnvelope> VaultMemberKeyEnvelopes => Set<VaultMemberKeyEnvelope>();
    public DbSet<VaultKeyRotation> VaultKeyRotations => Set<VaultKeyRotation>();
    public DbSet<VaultKeyRotationPreparedItem> VaultKeyRotationPreparedItems => Set<VaultKeyRotationPreparedItem>();
    public DbSet<VaultKeyMaterialEnvelope> VaultKeyMaterialEnvelopes => Set<VaultKeyMaterialEnvelope>();
    public DbSet<VaultPrincipalDeprovisioning> VaultPrincipalDeprovisionings => Set<VaultPrincipalDeprovisioning>();
    public DbSet<VaultOrganizationLifecycle> VaultOrganizationLifecycles => Set<VaultOrganizationLifecycle>();
    public DbSet<VaultCreationChallenge> VaultCreationChallenges => Set<VaultCreationChallenge>();
    public DbSet<EntryCreationChallenge> EntryCreationChallenges => Set<EntryCreationChallenge>();
    public DbSet<MemberKeyDirectoryEntry> MemberKeyDirectory => Set<MemberKeyDirectoryEntry>();
    public DbSet<VaultEntry> Entries => Set<VaultEntry>();
    public DbSet<VaultEntryKey> EntryKeys => Set<VaultEntryKey>();
    public DbSet<VaultEntryVersion> EntryVersions => Set<VaultEntryVersion>();
    public DbSet<AgentVaultDiscoveryEnvelope> AgentVaultDiscoveryEnvelopes => Set<AgentVaultDiscoveryEnvelope>();
    public DbSet<Grant> Grants => Set<Grant>();
    public DbSet<GrantEntryScope> GrantEntryScopes => Set<GrantEntryScope>();
    public DbSet<GrantEntryEnvelope> GrantEntryEnvelopes => Set<GrantEntryEnvelope>();
    public DbSet<FullGrantPreparation> FullGrantPreparations => Set<FullGrantPreparation>();
    public DbSet<FullGrantPreparationEntry> FullGrantPreparationEntries => Set<FullGrantPreparationEntry>();
    public DbSet<EncryptedReasonEnvelope> EncryptedReasonEnvelopes => Set<EncryptedReasonEnvelope>();
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<User> Users => Set<User>();
    public DbSet<CredentialFailureReport> CredentialFailureReports => Set<CredentialFailureReport>();
    public DbSet<EncryptedPresentationAsset> EncryptedPresentationAssets => Set<EncryptedPresentationAsset>();
    public DbSet<VaultPresentationAssetCutoverState> VaultPresentationAssetCutoverStates => Set<VaultPresentationAssetCutoverState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("pg_trgm");

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(VaultDbWriteContext).Assembly);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        throw new ReadOnlyContextSaveChangesException(nameof(VaultDbReadContext));
}
