using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Features;

internal sealed record EntryCreationMaterial(
    VaultEntryKey EntryKey,
    MemberIndexCiphertext MemberIndex,
    MemberSecretCiphertext MemberSecret,
    AgentDiscoveryCiphertext? AgentDiscovery)
{
    internal static EntryCreationMaterial From(CreateEntryRequest req) => new(
        VaultEnvelopeContractMapper.ToDomain(req.EntryKey),
        VaultEnvelopeContractMapper.ToDomain(req.MemberIndex),
        VaultEnvelopeContractMapper.ToDomain(req.MemberSecret),
        req.AgentDiscovery is null ? null : VaultEnvelopeContractMapper.ToDomain(req.AgentDiscovery));
}

// Stages a canonical Entry creation on the caller's write context without committing, so a caller can
// combine it with other domain changes in one CommitAsync.
internal static class EntryCreation
{
    internal static Task<VaultEntry?> FindAsync(
        VaultDomainWriteContext domainWriteContext,
        Guid organizationId,
        Guid vaultId,
        Guid entryId,
        CancellationToken ct) =>
        domainWriteContext.Entries
            .Include(x => x.Keys)
            .Include(x => x.Versions)
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId
                                      && x.VaultId == vaultId
                                      && x.Id == entryId, ct);

    internal static async Task<VaultEntry?> StageAsync(
        VaultDomainWriteContext domainWriteContext,
        CreateEntryRequest req,
        EntryCreationMaterial material,
        Guid organizationId,
        Guid userId,
        Instant now,
        CancellationToken ct)
    {
        var vault = await domainWriteContext.Vaults
            .Where(x => x.OrganizationId == organizationId && x.Id == req.VaultId)
            .SingleOrDefaultAsync(ct);
        if (vault is null)
        {
            return null;
        }

        var scope = new EntryScope(vault.OrganizationId, vault.Id, req.EntryId);
        var challenge = await domainWriteContext.EntryCreationChallenges
            .FirstOrDefaultAsync(x => x.OrganizationId == scope.OrganizationId
                                      && x.VaultId == scope.VaultId
                                      && x.EntryId == scope.EntryId, ct)
            ?? throw new DomainException("A valid Entry creation challenge is required.");

        challenge.Consume(scope, userId, now);
        var sequences = vault.AllocateSequences(material.AgentDiscovery is not null, userId, now);
        var version = VaultEntryVersion.Create(
            material.MemberSecret,
            sequences,
            memberIndexChanged: true,
            now,
            Palladin.Core.Types.ActorType.Member,
            userId);
        var entry = VaultEntry.Create(
            scope,
            material.EntryKey,
            version,
            material.MemberIndex,
            material.AgentDiscovery,
            req.DeliveryPolicy,
            vault.MemberKeyGeneration,
            vault.CurrentVaultKeyVersion,
            vault.CurrentVdkVersion,
            now,
            userId);

        domainWriteContext.Add(entry);
        return entry;
    }
}
