using Palladin.Module.Agents.Infrastructure.AgentAuth;
using Palladin.Module.Agents.Infrastructure.DiscoveryMaps;
using Palladin.Module.Agents.Infrastructure.Persistence;
using Palladin.Module.Agents.Infrastructure.PublicAssets;
using Palladin.Module.Agents.Infrastructure.Pairing;
using Palladin.Module.Agents.Features;
using Palladin.Core.Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Palladin.Module.Agents.Infrastructure;

internal static class InfrastructureModule
{
    internal static IServiceCollection AddAgentsInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddAgentsPersistence(configuration);
        services.AddAgentAuth(configuration);
        services.AddOptions<AgentPairingOptions>()
            .Bind(configuration.GetSection(AgentPairingOptions.Position))
            .Validate(options =>
                AgentPairingOptions.IsValidApprovalUrlBase(options.ApprovalUrlBase)
                && options.Lifetime >= TimeSpan.FromMinutes(1)
                && options.Lifetime <= TimeSpan.FromMinutes(30)
                && options.PollIntervalMilliseconds is >= 500 and <= 10_000,
                "Agent pairing options must use an absolute approval URL and bounded timing values.")
            .ValidateOnStart();
        services.AddSingleton<AgentPairingCredentialProtector>();
        var pairingCleanupSection = configuration.GetSection(CleanupAgentPairingsJobOptions.Position);
        services.AddScopedCronJob<CleanupAgentPairingsJob, CleanupAgentPairingsJobOptions>(pairingCleanupSection);
        services.AddOptions<CleanupAgentPairingsJobOptions>()
            .Validate(options => !options.Enabled
                || (!string.IsNullOrWhiteSpace(options.Expression)
                    && options.TerminalRetentionMinutes is >= 5 and <= 1440
                    && options.BatchSize is > 0 and <= 5000),
                "Agent pairing cleanup requires a valid schedule, retrieval grace, and bounded batch size.")
            .ValidateOnStart();
        services.AddOptions<FormDiscoveryMapOptions>()
            .Bind(configuration.GetSection(FormDiscoveryMapOptions.Position))
            .Validate(options => options.MaximumDefinitionBytes is > 0 and <= 65_536
                && options.MaximumLoginUrlBytes is > 0 and <= 2_048
                && options.MaximumProviderLength is > 0 and <= 64
                && options.MaximumJsonDepth is > 0 and <= 32
                && options.MaximumSteps is > 0 and <= 8
                && options.MaximumFields is > 0 and <= 16
                && options.MaximumFieldIdLength is > 0 and <= 128
                && options.MaximumSelectorBytes is > 0 and <= 1_024
                && options.MaximumCookieOverlays is >= 0 and <= 4
                && options.MaximumSelectorsPerOverlay is >= 0 and <= 8
                && options.MinimumWaitTimeoutMilliseconds is >= 100 and <= 60_000
                && options.MaximumWaitTimeoutMilliseconds >= options.MinimumWaitTimeoutMilliseconds
                && options.MaximumWaitTimeoutMilliseconds <= 60_000
                && options.MaximumLookupRevisions is > 0 and <= 100,
                "Form Discovery Map limits must stay within the provider-neutral wire contract.")
            .ValidateOnStart();
        services.AddSingleton<FormDiscoveryMapContract>();
        services.AddOptions<PublicAssetCatalogClientOptions>().Bind(configuration.GetSection(PublicAssetCatalogClientOptions.Position));
        services.AddHttpClient<IPublicAssetCatalogClient, PublicAssetCatalogClient>();

        return services;
    }
}
