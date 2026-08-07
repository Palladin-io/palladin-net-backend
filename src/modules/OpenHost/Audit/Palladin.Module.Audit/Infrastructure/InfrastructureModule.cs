using Palladin.Module.Audit.Features.Export;
using Palladin.Module.Audit.Infrastructure.Persistence;
using Palladin.Module.Audit.Infrastructure.Exports;
using Palladin.Core.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Palladin.Module.Audit.Infrastructure;

internal static class InfrastructureModule
{
    // Retention: MVP keeps audit logs indefinitely (no rotation), per the Audit brain note. A
    // configurable retention/rotation policy is a Phase-2 follow-up.
    internal static IServiceCollection AddAuditInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddAuditPersistence(configuration);
        services.AddOptions<AuditExportStorageOptions>()
            .Bind(configuration.GetSection(AuditExportStorageOptions.Position))
            .Validate(x => !string.IsNullOrWhiteSpace(x.Region)
                           && x.DownloadUrlLifetimeMinutes is > 0 and <= 15,
                "Audit export storage requires a region and a short download URL lifetime.")
            .ValidateOnStart();
        services.AddScoped<IAuditExportJobRunner, AuditExportJobRunner>();
        services.AddSingleton<IAuditExportStorage, S3AuditExportStorage>();
        return services;
    }
}
