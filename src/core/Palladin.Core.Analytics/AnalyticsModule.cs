using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PostHog;

namespace Palladin.Core.Analytics;

public static class AnalyticsModule
{
    public static IHostApplicationBuilder AddAnalyticsModule(this IHostApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection(PostHogOptions.Position).Get<PostHogOptions>();

        // Without a project API key the PostHog batch handler throws on every flush
        // (local/dev has no key by design) — swap in a no-op sink instead.
        if (string.IsNullOrWhiteSpace(options?.ProjectApiKey))
        {
            builder.Services.AddSingleton<IAnalyticsService, NoOpAnalyticsService>();
            return builder;
        }

        builder.AddPostHog();
        builder.Services.AddScoped<IAnalyticsService, PostHogAnalyticsService>();
        return builder;
    }
}
