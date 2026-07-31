using MassTransit;

namespace Palladin.Core.MassTransit.BuildingBlocks;

internal static class CircuitBreakerBuildingBlock
{
    internal static IRabbitMqBusFactoryConfigurator ConfigureCircuitBreaker(
        this IRabbitMqBusFactoryConfigurator configure,
        MassTransitOptions options
    )
    {
        if (options.CircuitBreaker.Disabled)
        {
            return configure;
        }

        configure.UseCircuitBreaker(
            circuitBreakerOptions =>
            {
                circuitBreakerOptions.TripThreshold = options.CircuitBreaker.TripThreshold;
                circuitBreakerOptions.ActiveThreshold = options.CircuitBreaker.ActiveThresholdMessages;
                circuitBreakerOptions.ResetInterval = options.CircuitBreaker.ResetInterval;
                circuitBreakerOptions.TrackingPeriod = options.CircuitBreaker.TrackingPeriod;
            }
        );

        return configure;
    }
}
