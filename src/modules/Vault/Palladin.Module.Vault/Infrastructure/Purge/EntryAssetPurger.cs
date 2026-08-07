using Palladin.Module.Vault.Infrastructure.Assets;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Vault.Infrastructure.Purge;

internal interface IEntryAssetPurger
{
    Task PurgeAsync(EntryScope scope, CancellationToken cancellationToken);
    Task PurgeVaultAsync(Guid organizationId, Guid vaultId, CancellationToken cancellationToken);
}

internal sealed class CdnEntryAssetPurger(
    VaultDomainWriteContext domainWriteContext,
    IEncryptedPresentationAssetStorage assetStorage) : IEntryAssetPurger
{
    public async Task PurgeAsync(EntryScope scope, CancellationToken cancellationToken)
    {
        var assets = await domainWriteContext.EncryptedPresentationAssets
            .Where(x => x.OrganizationId == scope.OrganizationId
                        && x.VaultId == scope.VaultId
                        && x.EntryId == scope.EntryId)
            .ToListAsync(cancellationToken);
        foreach (var asset in assets)
        {
            await assetStorage.DeleteAllVersionsAsync(asset.StorageKey, cancellationToken);
            domainWriteContext.Remove(asset);
        }

        await assetStorage.DeleteAllVersionsAsync(
            $"entry-icons/{scope.VaultId}/{scope.EntryId}",
            cancellationToken);
    }

    public async Task PurgeVaultAsync(
        Guid organizationId,
        Guid vaultId,
        CancellationToken cancellationToken)
    {
        var assets = await domainWriteContext.EncryptedPresentationAssets
            .Where(x => x.OrganizationId == organizationId && x.VaultId == vaultId)
            .ToListAsync(cancellationToken);
        foreach (var asset in assets)
        {
            await assetStorage.DeleteAllVersionsAsync(asset.StorageKey, cancellationToken);
            domainWriteContext.Remove(asset);
        }

        await assetStorage.DeleteAllVersionsAsync($"entry-icons/{vaultId}/", cancellationToken);
        await assetStorage.DeleteAllVersionsAsync($"vault-icons/{vaultId}/", cancellationToken);
    }
}
