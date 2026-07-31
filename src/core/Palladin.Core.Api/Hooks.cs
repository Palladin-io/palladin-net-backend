using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Palladin.Core.Api;

public interface IApplicationStartingHook
{
    Task OnApplicationStartingAsync(CancellationToken cancellationToken);
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationStartingHook<THook>(
        this IServiceCollection services)
        where THook : class, IApplicationStartingHook
    {
        services.AddScoped<IApplicationStartingHook, THook>();
        return services;
    }
}

public static class WebApplicationExtensions
{
    public static async Task StartBeforeApplicationStartedHooksAsync(this WebApplication webApplication,
        CancellationToken cancellationToken = default)
    {
        await using var scope = webApplication.Services.CreateAsyncScope();

        var hooks = scope.ServiceProvider.GetServices<IApplicationStartingHook>();
        foreach (var hook in hooks)
        {
            await hook.OnApplicationStartingAsync(cancellationToken);
        }
    }
}
