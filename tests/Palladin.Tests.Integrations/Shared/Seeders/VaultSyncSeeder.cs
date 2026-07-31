using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared.Fakers;

namespace Palladin.Tests.Integrations.Shared.Seeders;

internal static class VaultSyncSeeder
{
    internal static async Task<Guid> SeedSyncEntryAsync(
        this IServiceProvider serviceProvider,
        Guid organizationId,
        Guid vaultId,
        Guid actorId,
        bool includeDiscovery = true,
        int seed = 0)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
        var vault = await writeContext.Vaults.SingleAsync(x =>
            x.OrganizationId == organizationId && x.Id == vaultId);
        var entryId = Guid.NewGuid();
        var request = EntryEnvelopeFaker.CreateRequest(
            organizationId,
            vaultId,
            entryId,
            includeDiscovery,
            seed);
        var memberSecret = VaultEnvelopeContractMapper.ToDomain(request.MemberSecret);
        var memberIndex = VaultEnvelopeContractMapper.ToDomain(request.MemberIndex);
        var agentDiscovery = request.AgentDiscovery is null
            ? null
            : VaultEnvelopeContractMapper.ToDomain(request.AgentDiscovery);
        var now = SystemClock.Instance.GetCurrentInstant();
        var sequences = vault.AllocateSequences(agentDiscovery is not null, actorId, now);
        var version = VaultEntryVersion.Create(
            memberSecret,
            sequences,
            memberIndexChanged: true,
            now,
            ActorType.Member,
            actorId);
        var entry = VaultEntry.Create(
            new EntryScope(organizationId, vaultId, entryId),
            VaultEnvelopeContractMapper.ToDomain(request.EntryKey),
            version,
            memberIndex,
            agentDiscovery,
            vault.MemberKeyGeneration,
            vault.CurrentVaultKeyVersion,
            vault.CurrentVdkVersion,
            now,
            actorId);
        writeContext.Entries.Add(entry);
        await writeContext.SaveChangesAsync();
        return entryId;
    }

    internal static async Task SeedSyncEntryUpdateAsync(
        this IServiceProvider serviceProvider,
        Guid organizationId,
        Guid vaultId,
        Guid entryId,
        Guid actorId,
        ulong baseRevision,
        ulong? memberIndexRevision = null,
        ulong? agentDiscoveryRevision = null,
        uint? newKeyVersion = null,
        bool disableDiscovery = false,
        int seed = 32)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
        var vault = await writeContext.Vaults.SingleAsync(x =>
            x.OrganizationId == organizationId && x.Id == vaultId);
        var entry = await writeContext.Entries
            .Include(x => x.Keys)
            .Include(x => x.Versions)
            .SingleAsync(x => x.OrganizationId == organizationId
                              && x.VaultId == vaultId
                              && x.Id == entryId);
        var request = EntryEnvelopeFaker.CreateUpdateRequest(
            organizationId,
            vaultId,
            entryId,
            baseRevision,
            memberIndexRevision,
            agentDiscoveryRevision,
            newKeyVersion: newKeyVersion,
            disableDiscovery: disableDiscovery,
            seed: seed);
        var now = SystemClock.Instance.GetCurrentInstant();
        var sequences = vault.AllocateSequences(request.AgentDiscoveryChanged, actorId, now);
        entry.Update(
            new EntryRevision(baseRevision),
            vault.MemberKeyGeneration,
            vault.CurrentVaultKeyVersion,
            vault.CurrentVdkVersion,
            request.NewEntryKey is null ? null : VaultEnvelopeContractMapper.ToDomain(request.NewEntryKey),
            VaultEnvelopeContractMapper.ToDomain(request.MemberSecret),
            request.MemberIndex is null ? null : VaultEnvelopeContractMapper.ToDomain(request.MemberIndex),
            request.AgentDiscoveryChanged,
            request.AgentDiscovery is null ? null : VaultEnvelopeContractMapper.ToDomain(request.AgentDiscovery),
            sequences,
            now,
            actorId);
        await writeContext.SaveChangesAsync();
    }

    internal static async Task SeedAgentDiscoveryProvisioningAsync(
        this IServiceProvider serviceProvider,
        ProvisionAgentDiscoveryRequest request,
        Guid actorId)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
        var vault = await writeContext.Vaults
            .Include(x => x.AgentVaultDiscoveryEnvelopes)
            .SingleAsync(x => x.OrganizationId == request.Envelope.OrganizationId
                              && x.Id == request.VaultId);
        var agent = await writeContext.Agents.SingleAsync(x =>
            x.OrganizationId == request.Envelope.OrganizationId && x.Id == request.AgentId);
        var provisioning = VaultManifestCryptoValidator.Validate(request.Envelope, request.Manifest, agent);
        vault.ProvisionAgentDiscovery(agent, provisioning, actorId, SystemClock.Instance.GetCurrentInstant());
        await writeContext.SaveChangesAsync();
    }
}
