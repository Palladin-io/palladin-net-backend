using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Palladin.Module.Identity.Infrastructure.BrowserSessions;
using Palladin.Core.MassTransit;
using Palladin.Module.Identity.Infrastructure;
using JetBrains.Annotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Palladin.Module.Identity;

[PublicAPI]
public static class IdentityModule
{
    public static IApplicationBuilder UseBrowserSessionBoundary(this IApplicationBuilder app, IReadOnlyCollection<string> allowedOrigins) =>
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api/browser"))
            {
                context.Response.Headers.CacheControl = "no-store";
                if (!BrowserSessionOriginGuard.IsAllowed(context, allowedOrigins))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
            }
            await next(context);
        });

    public static IServiceCollection AddIdentityModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddIdentityInfrastructure(configuration);
        services.AddMassTransitAssembly(typeof(IdentityModule).Assembly);

        return services;
    }
}
