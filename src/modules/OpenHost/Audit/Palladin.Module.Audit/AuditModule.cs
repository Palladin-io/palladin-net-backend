using Palladin.Core.MassTransit;
using Palladin.Module.Audit.Infrastructure;
using JetBrains.Annotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Palladin.Module.Audit;

[PublicAPI]
public static class AuditModule
{
    public static IServiceCollection AddAuditModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddAuditInfrastructure(configuration);
        services.AddMassTransitAssembly(typeof(AuditModule).Assembly);

        return services;
    }
}
