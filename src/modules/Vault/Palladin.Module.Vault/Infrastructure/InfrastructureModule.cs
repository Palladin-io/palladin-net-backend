using Amazon;
using Amazon.S3;
using Amazon.SimpleSystemsManagement;
using Palladin.Core.Api;
using Palladin.Core.Hangfire;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.Assets;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Creation;
using Palladin.Module.Vault.Infrastructure.History;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Purge;
using Palladin.Module.Vault.Infrastructure.Sync;
using Palladin.Module.Vault.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Palladin.Module.Vault.Infrastructure;

internal static class InfrastructureModule
{
    internal static IServiceCollection AddVaultInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddVaultPersistence(configuration);
        services.AddVaultCrypto(configuration);
        services.AddOptions<VaultCreationOptions>()
            .Bind(configuration.GetSection(VaultCreationOptions.Position))
            .Validate(options => options.ChallengeTtlSeconds is > 0 and <= 3600,
                "Vault creation challenge TTL must be between 1 and 3600 seconds.")
            .ValidateOnStart();
        services.AddOptions<VaultHistoryOptions>()
            .Bind(configuration.GetSection(VaultHistoryOptions.Position))
            .Validate(options => options.MaximumVersions is > 1 and <= 100
                                 && options.MaximumAgeDays is > 0 and <= 365
                                 && options.DefaultPageSize > 0
                                 && options.DefaultPageSize <= options.MaximumPageSize
                                 && options.MaximumPageSize is > 0 and <= 100,
                "Vault history policy is outside the frozen product bounds.")
            .ValidateOnStart();
        services.AddOptions<VaultEntryLifecycleOptions>()
            .Bind(configuration.GetSection(VaultEntryLifecycleOptions.Position))
            .Validate(options => options.Enabled
                                 && options.RecentlyDeletedDays == 30
                                 && options.BatchSize is > 0 and <= 1000
                                 && !string.IsNullOrWhiteSpace(options.Expression),
                "Vault Entry lifecycle policy is outside the frozen product bounds.")
            .ValidateOnStart();
        services.AddOptions<EntryPurgeLedgerOptions>()
            .Bind(configuration.GetSection(EntryPurgeLedgerOptions.Position))
            .Validate(options => !options.Enabled
                                 || (!string.IsNullOrWhiteSpace(options.BucketName)
                                     && !string.IsNullOrWhiteSpace(options.Region)
                                     && !string.IsNullOrWhiteSpace(options.ObjectPrefix)
                                     && !string.IsNullOrWhiteSpace(options.KeyParameterPrefix)
                                     && options.CurrentKeyVersion > 0
                                     && options.RetentionDays == 120),
                "Enabled Vault Entry purge ledger requires its frozen S3, SSM key and retention settings.")
            .ValidateOnStart();
        services.AddOptions<VaultAssetStorageOptions>()
            .Bind(configuration.GetSection(VaultAssetStorageOptions.Position))
            .Validate(x => !string.IsNullOrWhiteSpace(x.Region)
                           && x.DownloadUrlLifetimeMinutes is > 0 and <= 15,
                "Vault encrypted asset storage requires a region and a short download URL lifetime.")
            .ValidateOnStart();

        services.Configure<GrantApprovalWaitOptions>(
            configuration.GetSection(GrantApprovalWaitOptions.Position));

        services.AddScopedCronJob<ExpireGrantsJob, ExpireGrantsJobOptions>(
            configuration.GetSection(ExpireGrantsJobOptions.Position));
        services.AddScopedCronJob<CleanupFullGrantPreparationsJob, CleanupFullGrantPreparationsJobOptions>(
            configuration.GetSection(CleanupFullGrantPreparationsJobOptions.Position));
        services.AddScopedCronJob<VaultEntryLifecycleJob, VaultEntryLifecycleOptions>(
            configuration.GetSection(VaultEntryLifecycleOptions.Position));
        services.AddOptions<DispatchRoleVaultAccessPoliciesJobOptions>()
            .Bind(configuration.GetSection(DispatchRoleVaultAccessPoliciesJobOptions.Position))
            .Validate(options => options.BatchSize is > 0 and <= 1000
                                 && !string.IsNullOrWhiteSpace(options.Expression),
                "Role Vault-access policy dispatch settings are outside the supported bounds.")
            .ValidateOnStart();
        services.AddScopedCronJob<DispatchRoleVaultAccessPoliciesJob, DispatchRoleVaultAccessPoliciesJobOptions>(
            configuration.GetSection(DispatchRoleVaultAccessPoliciesJobOptions.Position));

        services.AddScoped<IVaultDirectory, VaultDirectory>();
        services.AddScoped<CredentialDeliveryService>();
        services.AddScoped<VaultPrincipalDeprovisioningCoordinator>();
        services.AddScoped<RoleVaultAccessReconciler>();
        services.AddScoped<EntryLifecycleService>();
        services.AddScoped<EntryPurgeService>();
        services.AddScoped<IEntryAssetPurger, CdnEntryAssetPurger>();
        services.AddScoped<LegacyPresentationAssetPurger>();
        services.AddSingleton<S3VaultAssetStorage>();
        services.AddSingleton<IEncryptedPresentationAssetStorage>(x => x.GetRequiredService<S3VaultAssetStorage>());
        services.AddSingleton<ILegacyPresentationAssetStorage>(x => x.GetRequiredService<S3VaultAssetStorage>());
        services.AddApplicationStartingHook<PurgeLegacyPresentationAssetsApplicationStartingHook>();
        services.AddSingleton<IAmazonS3>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<EntryPurgeLedgerOptions>>().Value;
            return new AmazonS3Client(RegionEndpoint.GetBySystemName(options.Region));
        });
        services.AddSingleton<IAmazonSimpleSystemsManagement>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<EntryPurgeLedgerOptions>>().Value;
            return new AmazonSimpleSystemsManagementClient(RegionEndpoint.GetBySystemName(options.Region));
        });
        services.AddSingleton<IEntryPurgeKeyProvider, SsmEntryPurgeKeyProvider>();
        services.AddSingleton<EntryPurgeTokenGenerator>();
        services.AddSingleton<IEntryPurgeLedger, S3EntryPurgeLedger>();
        services.AddApplicationStartingHook<ReplayEntryPurgeLedgerApplicationStartingHook>();
        services.AddSingleton<VaultSyncCursorProtector>();
        return services;
    }
}
