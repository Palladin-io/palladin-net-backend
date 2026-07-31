using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Palladin.Core.Persistence;

public static class HealthCheckExtensions
{
    public static IHealthChecksBuilder AddModuleNpgSql<TOptions>(this IHealthChecksBuilder builder, string moduleName)
        where TOptions : class, IPersistenceOptions =>
        builder.AddNpgSql(
            sp =>
            {
                var persistenceOptions = sp.GetRequiredService<IOptions<TOptions>>().Value;
                return persistenceOptions.ConnectionString;
            },
            name: $"{moduleName}:npgsql"
        );
}
