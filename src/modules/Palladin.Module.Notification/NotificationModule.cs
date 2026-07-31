using Palladin.Core.MassTransit;
using Palladin.Module.Notification.Infrastructure;
using Palladin.Module.Notification.Infrastructure.SignalR;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Palladin.Module.Notification;

[PublicAPI]
public static class NotificationModule
{
    public const string HubPath = "/hubs/notifications";

    public static IServiceCollection AddNotificationModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddNotificationInfrastructure(configuration);
        services.AddMassTransitAssembly(typeof(NotificationModule).Assembly);

        return services;
    }

    public static IEndpointRouteBuilder UseNotificationModule(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHub<NotificationHub>(HubPath);

        return endpoints;
    }
}
