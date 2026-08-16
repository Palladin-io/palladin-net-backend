using Palladin.Core.Persistence;
using Palladin.Module.Vault.Domain;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Vault.Infrastructure.Persistence;

internal sealed class VaultDomainReadContext(VaultDbReadContext readContext) : DomainReadContextBase(readContext)
{
    public IQueryable<Domain.Vault> Vaults => Query<Domain.Vault>();
    public IQueryable<VaultMember> VaultMembers => Query<VaultMember>();
    public IQueryable<VaultMemberKeyEnvelope> VaultMemberKeyEnvelopes => Query<VaultMemberKeyEnvelope>();
    public IQueryable<VaultKeyRotation> VaultKeyRotations => Query<VaultKeyRotation>();
    public IQueryable<VaultKeyRotationPreparedItem> VaultKeyRotationPreparedItems => Query<VaultKeyRotationPreparedItem>();
    public IQueryable<VaultKeyMaterialEnvelope> VaultKeyMaterialEnvelopes => Query<VaultKeyMaterialEnvelope>();
    public IQueryable<VaultPrincipalDeprovisioning> VaultPrincipalDeprovisionings => Query<VaultPrincipalDeprovisioning>();
    public IQueryable<VaultOrganizationLifecycle> VaultOrganizationLifecycles => Query<VaultOrganizationLifecycle>();
    public IQueryable<VaultCreationChallenge> VaultCreationChallenges => Query<VaultCreationChallenge>();
    public IQueryable<EntryCreationChallenge> EntryCreationChallenges => Query<EntryCreationChallenge>();
    public IQueryable<MemberKeyDirectoryEntry> MemberKeyDirectory => Query<MemberKeyDirectoryEntry>();
    public IQueryable<VaultEntry> Entries => Query<VaultEntry>();
    public IQueryable<VaultEntryKey> EntryKeys => Query<VaultEntryKey>();
    public IQueryable<VaultEntryVersion> EntryVersions => Query<VaultEntryVersion>();
    public IQueryable<AgentVaultDiscoveryEnvelope> AgentVaultDiscoveryEnvelopes => Query<AgentVaultDiscoveryEnvelope>();
    public IQueryable<Grant> Grants => Query<Grant>();
    public IQueryable<GrantEntryScope> GrantEntryScopes => Query<GrantEntryScope>();
    public IQueryable<GrantEntryEnvelope> GrantEntryEnvelopes => Query<GrantEntryEnvelope>();
    public IQueryable<FullGrantPreparation> FullGrantPreparations => Query<FullGrantPreparation>();
    public IQueryable<FullGrantPreparationEntry> FullGrantPreparationEntries => Query<FullGrantPreparationEntry>();
    public IQueryable<EncryptedReasonEnvelope> EncryptedReasonEnvelopes => Query<EncryptedReasonEnvelope>();
    public IQueryable<Agent> Agents => Query<Agent>();
    public IQueryable<User> Users => Query<User>();
    public IQueryable<CredentialFailureReport> CredentialFailureReports => Query<CredentialFailureReport>();
    public IQueryable<EncryptedPresentationAsset> EncryptedPresentationAssets => Query<EncryptedPresentationAsset>();

    public IQueryable<RotationEntryKeyRow> GetRotationEntryKeyPage(
        Guid organizationId,
        Guid vaultId,
        Guid? afterEntryId,
        uint? afterKeyVersion,
        int take)
    {
        if (afterEntryId is not { } entryId)
        {
            return readContext.Database.SqlQuery<RotationEntryKeyRow>(
                $"""
                 SELECT
                     "OrganizationId", "VaultId", "EntryId",
                     "WrapperRevision", "KeyVersion", "MemberKeyGeneration", "WrappingKeyVersion",
                     "ProtocolVersion", "CryptoSuiteId", "EncodedSuitePayload"
                 FROM "VaultEntryKeys"
                 WHERE "OrganizationId" = {organizationId}
                   AND "VaultId" = {vaultId}
                 ORDER BY "EntryId", "KeyVersion"
                 LIMIT {take}
                 """);
        }

        return readContext.Database.SqlQuery<RotationEntryKeyRow>(
            $"""
             SELECT
                 "OrganizationId", "VaultId", "EntryId",
                 "WrapperRevision", "KeyVersion", "MemberKeyGeneration", "WrappingKeyVersion",
                 "ProtocolVersion", "CryptoSuiteId", "EncodedSuitePayload"
             FROM "VaultEntryKeys"
             WHERE "OrganizationId" = {organizationId}
               AND "VaultId" = {vaultId}
               AND ("EntryId" > {entryId}
                    OR ("EntryId" = {entryId} AND "KeyVersion" > {(decimal)(afterKeyVersion ?? 0)}))
             ORDER BY "EntryId", "KeyVersion"
             LIMIT {take}
             """);
    }

    public IQueryable<VaultEntryVersion> GetEntryHistoryPage(
        Guid organizationId,
        Guid vaultId,
        Guid entryId,
        ulong beforeRevision,
        int take) =>
        readContext.EntryVersions.FromSqlInterpolated(
            $"""
             SELECT * FROM "VaultEntryVersions"
             WHERE "OrganizationId" = {organizationId}
               AND "VaultId" = {vaultId}
               AND "EntryId" = {entryId}
               AND "Revision" < {(decimal)beforeRevision}
             ORDER BY "Revision" DESC
             LIMIT {take}
             """);

}

internal sealed record RotationEntryKeyRow(
    Guid OrganizationId,
    Guid VaultId,
    Guid EntryId,
    decimal WrapperRevision,
    decimal KeyVersion,
    decimal MemberKeyGeneration,
    decimal WrappingKeyVersion,
    int ProtocolVersion,
    string CryptoSuiteId,
    byte[] EncodedSuitePayload);
