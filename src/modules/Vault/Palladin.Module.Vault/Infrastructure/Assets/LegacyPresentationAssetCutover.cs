using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NodaTime;
using Palladin.Core.Api;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Infrastructure.Assets;

internal sealed class LegacyPresentationAssetPurger(ILegacyPresentationAssetStorage storage)
{
    private static readonly string[] LegacyPrefixes = ["favicons/", "entry-icons/", "vault-icons/"];

    internal async Task PurgeAndVerifyAsync(CancellationToken cancellationToken)
    {
        foreach (var prefix in LegacyPrefixes)
        {
            await storage.DeleteAllVersionsAsync(prefix, cancellationToken);
            if (await storage.HasVersionsAsync(prefix, cancellationToken))
            {
                throw new InvalidOperationException(
                    "The zero-knowledge cutover cannot continue while legacy presentation asset versions remain in object storage.");
            }
        }
    }
}

internal sealed class PurgeLegacyPresentationAssetsApplicationStartingHook(
    VaultDomainWriteContext domainWriteContext,
    LegacyPresentationAssetPurger purger,
    IConfiguration configuration,
    IHostEnvironment environment,
    IClock clock) : IApplicationStartingHook
{
    public async Task OnApplicationStartingAsync(CancellationToken cancellationToken)
    {
        var state = await domainWriteContext.VaultPresentationAssetCutoverStates.SingleAsync(
            x => x.Id == VaultPresentationAssetCutoverState.ZeroKnowledgeAssets,
            cancellationToken);
        if (state.LegacyObjectsPurgedAt is not null)
        {
            return;
        }

        var objectStorageConfigured = configuration.GetSection(VaultAssetStorageOptions.Position)["BucketName"] is { Length: > 0 };
        if (objectStorageConfigured)
        {
            await purger.PurgeAndVerifyAsync(cancellationToken);
        }
        else if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
        {
            throw new InvalidOperationException(
                "Object storage must be configured so the zero-knowledge cutover can verify deletion of legacy presentation assets.");
        }

        state.MarkLegacyObjectsPurged(clock.GetCurrentInstant());
        await domainWriteContext.CommitAsync(cancellationToken);
    }
}
