using Palladin.Core.Events;
using Palladin.Core.Persistence;
using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Persistence;

internal sealed class VaultDomainWriteContext(
    VaultDbWriteContext writeContext,
    IEnumerable<IEventPublisher> eventPublishers)
    : DomainWriteContextBase(writeContext, eventPublishers)
{
    public IQueryable<Domain.Vault> Vaults => Track<Domain.Vault>();
    public IQueryable<VaultMember> VaultMembers => Track<VaultMember>();
    public IQueryable<VaultMemberKeyEnvelope> VaultMemberKeyEnvelopes => Track<VaultMemberKeyEnvelope>();
    public IQueryable<VaultKeyRotation> VaultKeyRotations => Track<VaultKeyRotation>();
    public IQueryable<VaultKeyRotationPreparedItem> VaultKeyRotationPreparedItems => Track<VaultKeyRotationPreparedItem>();
    public IQueryable<VaultKeyMaterialEnvelope> VaultKeyMaterialEnvelopes => Track<VaultKeyMaterialEnvelope>();
    public IQueryable<VaultPrincipalDeprovisioning> VaultPrincipalDeprovisionings => Track<VaultPrincipalDeprovisioning>();
    public IQueryable<VaultCreationChallenge> VaultCreationChallenges => Track<VaultCreationChallenge>();
    public IQueryable<EntryCreationChallenge> EntryCreationChallenges => Track<EntryCreationChallenge>();
    public IQueryable<MemberKeyDirectoryEntry> MemberKeyDirectory => Track<MemberKeyDirectoryEntry>();
    public IQueryable<VaultEntry> Entries => Track<VaultEntry>();
    public IQueryable<VaultEntryKey> EntryKeys => Track<VaultEntryKey>();
    public IQueryable<VaultEntryVersion> EntryVersions => Track<VaultEntryVersion>();
    public IQueryable<AgentVaultDiscoveryEnvelope> AgentVaultDiscoveryEnvelopes => Track<AgentVaultDiscoveryEnvelope>();
    public IQueryable<AgentPairingActivation> AgentPairingActivations => Track<AgentPairingActivation>();
    public IQueryable<AgentPairingActivationCandidate> AgentPairingActivationCandidates => Track<AgentPairingActivationCandidate>();
    public IQueryable<Grant> Grants => Track<Grant>();
    public IQueryable<GrantEntryScope> GrantEntryScopes => Track<GrantEntryScope>();
    public IQueryable<GrantEntryEnvelope> GrantEntryEnvelopes => Track<GrantEntryEnvelope>();
    public IQueryable<EncryptedReasonEnvelope> EncryptedReasonEnvelopes => Track<EncryptedReasonEnvelope>();
    public IQueryable<Agent> Agents => Track<Agent>();
    public IQueryable<User> Users => Track<User>();
    public IQueryable<CredentialFailureReport> CredentialFailureReports => Track<CredentialFailureReport>();
    public IQueryable<EncryptedPresentationAsset> EncryptedPresentationAssets => Track<EncryptedPresentationAsset>();

    public IQueryable<Agent> LockAgent(Guid organizationId, Guid agentId) =>
        FromSqlInterpolated<Agent>(
            $"""
             SELECT * FROM "Agents"
             WHERE "OrganizationId" = {organizationId} AND "Id" = {agentId}
             FOR UPDATE
             """);

    public IQueryable<AgentPairingActivation> LockAgentPairingActivation(
        Guid organizationId,
        Guid agentId,
        Guid activationId) =>
        FromSqlInterpolated<AgentPairingActivation>(
            $"""
             SELECT * FROM "AgentPairingActivations"
             WHERE "OrganizationId" = {organizationId}
               AND "AgentId" = {agentId}
               AND "Id" = {activationId}
             FOR UPDATE
             """);

    public IQueryable<Domain.Vault> LockVault(Guid organizationId, Guid vaultId) =>
        FromSqlInterpolated<Domain.Vault>(
            $"""
             SELECT * FROM "Vaults"
             WHERE "OrganizationId" = {organizationId} AND "Id" = {vaultId}
             FOR UPDATE
             """);

    public IQueryable<VaultEntry> LockEntry(Guid organizationId, Guid vaultId, Guid entryId) =>
        FromSqlInterpolated<VaultEntry>(
            $"""
             SELECT * FROM "VaultEntries"
             WHERE "OrganizationId" = {organizationId}
               AND "VaultId" = {vaultId}
               AND "Id" = {entryId}
             FOR UPDATE
             """);

    public IQueryable<Guid> LockVaultGrantIds(Guid organizationId, Guid vaultId) =>
        SqlQuery<Guid>(
            $"""
             SELECT "Id" AS "Value" FROM "Grants"
             WHERE "OrganizationId" = {organizationId} AND "VaultId" = {vaultId}
             ORDER BY "Id"
             FOR UPDATE
             """);

    public IQueryable<MemberKeyDirectoryEntry> LockMemberKeyDirectory(Guid[] memberIds) =>
        FromSqlInterpolated<MemberKeyDirectoryEntry>(
            $"""
             SELECT * FROM "MemberKeyDirectory"
             WHERE "UserId" = ANY ({memberIds})
             ORDER BY "UserId"
             FOR UPDATE
             """);

    public IQueryable<int> LockOrganizationAgentLifecycle(Guid organizationId) =>
        SqlQuery<int>(
            $"""
             SELECT 1 AS "Value"
             FROM pg_advisory_xact_lock(hashtextextended({organizationId.ToString()}, 641083))
             """);

    public IQueryable<VaultKeyRotationPreparedItem> RequestedRotationItems(
        Guid organizationId,
        Guid vaultId,
        Guid rotationId,
        int[] kinds,
        Guid[] subjectIds,
        decimal[] subjectVersions) =>
        FromSqlInterpolated<VaultKeyRotationPreparedItem>(
            $"""
             SELECT item.*
             FROM "VaultKeyRotationPreparedItems" AS item
             JOIN unnest({kinds}, {subjectIds}, {subjectVersions})
                  AS requested("Kind", "SubjectId", "SubjectVersion")
               ON requested."Kind" = item."Kind"
              AND requested."SubjectId" = item."SubjectId"
              AND requested."SubjectVersion" = item."SubjectVersion"
             WHERE item."OrganizationId" = {organizationId}
               AND item."VaultId" = {vaultId}
               AND item."RotationId" = {rotationId}
             """);

    public void EnsureRotationPageTrackingIsBounded(int maximumEntries, int maximumGrants)
    {
        if (writeContext.ChangeTracker.Entries<VaultEntry>().Count() > maximumEntries
            || writeContext.ChangeTracker.Entries<Grant>().Count() > maximumGrants)
        {
            throw new InvalidOperationException("Vault key rotation exceeded its bounded EF tracking page.");
        }
    }

    public void EnsureRotationPreparedItemTrackingIsBounded(int maximumItems)
    {
        if (writeContext.ChangeTracker.Entries<VaultKeyRotationPreparedItem>().Count() > maximumItems)
        {
            throw new InvalidOperationException("Vault key rotation batch exceeded its bounded EF tracking set.");
        }
    }

    public void EnsureRotationRecipientTrackingIsBounded(int maximumMemberEnvelopes, int maximumAgentEnvelopes)
    {
        if (writeContext.ChangeTracker.Entries<VaultMemberKeyEnvelope>().Count() > maximumMemberEnvelopes
            || writeContext.ChangeTracker.Entries<AgentVaultDiscoveryEnvelope>().Count() > maximumAgentEnvelopes)
        {
            throw new InvalidOperationException("Vault key rotation exceeded its bounded recipient tracking page.");
        }
    }

    public Task<int> PruneStaleVaultKeyRotationItemsAsync(
        Guid organizationId,
        Guid vaultId,
        Guid rotationId,
        CancellationToken cancellationToken) =>
        ExecuteSqlInterpolatedAsync(
            $"""
             DELETE FROM "VaultKeyRotationPreparedItems" AS item
             WHERE item."OrganizationId" = {organizationId}
               AND item."VaultId" = {vaultId}
               AND item."RotationId" = {rotationId}
               AND (
                    (item."Kind" = {(int)VaultKeyRotationPreparedItemKind.VaultMetadata}
                     AND (item."SubjectId" <> {vaultId} OR item."SubjectVersion" <> 0))
                 OR (item."Kind" = {(int)VaultKeyRotationPreparedItemKind.MemberVaultKey}
                     AND (item."SubjectVersion" <> 0 OR NOT EXISTS (
                         SELECT 1 FROM "VaultMembers" AS member
                         WHERE member."OrganizationId" = item."OrganizationId"
                           AND member."VaultId" = item."VaultId"
                           AND member."UserId" = item."SubjectId")))
                 OR (item."Kind" = {(int)VaultKeyRotationPreparedItemKind.AgentDiscoveryEnvelope}
                     AND (item."SubjectVersion" <> 0 OR NOT EXISTS (
                         SELECT 1 FROM "Agents" AS agent
                         WHERE agent."OrganizationId" = item."OrganizationId"
                           AND agent."Id" = item."SubjectId"
                           AND agent."Status" = {(int)AgentStatus.Active})))
                 OR (item."Kind" = {(int)VaultKeyRotationPreparedItemKind.EntryKey}
                     AND NOT EXISTS (
                         SELECT 1 FROM "VaultEntryKeys" AS entry_key
                         WHERE entry_key."OrganizationId" = item."OrganizationId"
                           AND entry_key."VaultId" = item."VaultId"
                           AND entry_key."EntryId" = item."SubjectId"
                           AND entry_key."KeyVersion" = item."SubjectVersion"))
                 OR (item."Kind" = {(int)VaultKeyRotationPreparedItemKind.EntryDiscovery}
                     AND (item."SubjectVersion" <> 0 OR NOT EXISTS (
                         SELECT 1 FROM "VaultEntries" AS entry
                         WHERE entry."OrganizationId" = item."OrganizationId"
                           AND entry."VaultId" = item."VaultId"
                           AND entry."Id" = item."SubjectId"
                           AND entry."AgentDiscoveryRevision" IS NOT NULL)))
                 OR (item."Kind" = {(int)VaultKeyRotationPreparedItemKind.VaultKeyMaterial}
                     AND (item."SubjectId" <> {vaultId}
                          OR item."SubjectVersion" NOT BETWEEN 1 AND 3
                          OR NOT EXISTS (
                              SELECT 1 FROM "VaultKeyMaterialEnvelopes" AS material
                              WHERE material."OrganizationId" = item."OrganizationId"
                                AND material."VaultId" = item."VaultId"
                                AND material."Kind" = item."SubjectVersion")))
               )
             """,
            cancellationToken);

    public Task<int> PruneExcludedVaultKeyRotationItemsAsync(
        Guid organizationId,
        Guid vaultId,
        Guid rotationId,
        Guid? excludedMemberId,
        Guid? excludedAgentId,
        CancellationToken cancellationToken) =>
        ExecuteSqlInterpolatedAsync(
            $"""
             DELETE FROM "VaultKeyRotationPreparedItems"
             WHERE "OrganizationId" = {organizationId}
               AND "VaultId" = {vaultId}
               AND "RotationId" = {rotationId}
               AND (("Kind" = {(int)VaultKeyRotationPreparedItemKind.MemberVaultKey}
                     AND "SubjectId" = {excludedMemberId})
                 OR ("Kind" = {(int)VaultKeyRotationPreparedItemKind.AgentDiscoveryEnvelope}
                     AND "SubjectId" = {excludedAgentId}))
             """,
            cancellationToken);

    public Task<int> ResetVaultKeyRotationPreparedItemsAsync(
        Guid organizationId,
        Guid vaultId,
        Guid rotationId,
        CancellationToken cancellationToken) =>
        ExecuteSqlInterpolatedAsync(
            $"""
             DELETE FROM "VaultKeyRotationPreparedItems"
             WHERE "OrganizationId" = {organizationId}
               AND "VaultId" = {vaultId}
               AND "RotationId" = {rotationId}
             """,
            cancellationToken);
}
