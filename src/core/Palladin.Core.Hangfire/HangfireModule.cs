using Hangfire;
using Hangfire.Dashboard;
using Hangfire.PostgreSql;
using Palladin.Core.Api;
using Palladin.Core.Hangfire.CronJobs;
using Palladin.Core.Hangfire.Persistence;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Palladin.Core.Hangfire;

public static class HangfireModule
{
    public static IServiceCollection AddHangfireModule(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        var options = new HangfireOptions();
        configuration.Bind(options);

        services.Configure<HangfireOptions>(configuration);

        if (!options.Enabled)
        {
            return services;
        }

        services.AddApplicationStartingHook<DatabaseCreatorApplicationStartingHook>()
            .AddHangfire(
                x => x.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                    .UseSimpleAssemblyNameTypeSerializer()
                    .UseRecommendedSerializerSettings()
                    .UsePostgreSqlStorage(
                        options =>
                        {
                            options.UseNpgsqlConnection(GetConnectionString(configuration));
                        }
                    )
            )
            .AddHangfireServer()
            .AddSingleton<IJobStorage, JobStorageWrapper>()
            .AddHostedService<HangfireCronJobsInitializer>()
            .AddScoped<ICronJobExecutionTimeProvider>(
                _ => new ConnectionStringHangfireExecutionTimeProvider(GetConnectionString(configuration))
            );

        return services;
    }

    private static string GetConnectionString(IConfiguration configuration) =>
        configuration.GetValue<string>(nameof(HangfireOptions.ConnectionString))!;

    public static IServiceCollection AddScopedCronJob<TCronJob, TCronJobOptions>(
        this IServiceCollection services,
        IConfiguration configuration
    )
        where TCronJob : class, ICronJob
        where TCronJobOptions : class, ICronJobOptions
    {
        services.Configure<TCronJobOptions>(configuration);
        services.AddScopedCronJob<TCronJob>();

        return services;
    }

    internal static IServiceCollection AddScopedCronJob<TCronJob>(
        this IServiceCollection services
    )
        where TCronJob : class, ICronJob
    {
        services.AddScoped<TCronJob>();
        services.AddScoped<ICronJob, TCronJob>();

        return services;
    }

    public static IEndpointRouteBuilder UseHangfireModule(
        this IEndpointRouteBuilder endpoints
    )
    {
        var hangfireOptions = endpoints.ServiceProvider
            .GetRequiredService<IOptions<HangfireOptions>>();

        if (!hangfireOptions.Value.Enabled)
        {
            return endpoints;
        }

        endpoints.MapHangfireDashboard(hangfireOptions.Value.Url, new DashboardOptions
        {
            Authorization =
            [
                new HangfireBasicAuthorizationFilter(
                    hangfireOptions.Value.DashboardLogin,
                    hangfireOptions.Value.DashboardPassword)
            ]
        });

        return endpoints;
    }
}
