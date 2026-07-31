using MassTransit;

namespace Palladin.Core.MassTransit.BuildingBlocks;

internal static class RateLimiterBuildingBlock
{
    internal static IRabbitMqBusFactoryConfigurator ConfigureRateLimiter(
        this IRabbitMqBusFactoryConfigurator configure,
        MassTransitOptions options
    )
    {
        if (options.RateLimiter.Disabled)
        {
            return configure;
        }

        configure.UseRateLimit(options.RateLimiter.RateLimit, options.RateLimiter.TrackingPeriod);

        return configure;
    }
}
