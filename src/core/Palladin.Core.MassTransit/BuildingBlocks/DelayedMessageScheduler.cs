using MassTransit;

namespace Palladin.Core.MassTransit.BuildingBlocks;

internal static class DelayedMessageSchedulerBuildingBlock
{
    public static IRabbitMqBusFactoryConfigurator ConfigureDelayedMessageScheduler(
        this IRabbitMqBusFactoryConfigurator configure,
        MassTransitOptions options
    )
    {
        if (options.EnableDelayedMessageScheduler)
        {
            configure.UseDelayedMessageScheduler();
        }

        return configure;
    }
}
