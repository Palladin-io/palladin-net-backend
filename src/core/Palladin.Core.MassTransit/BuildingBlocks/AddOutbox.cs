using MassTransit;

namespace Palladin.Core.MassTransit.BuildingBlocks;

internal static class OutboxBuildingBlock
{
    internal static bool OutboxModuleWasLoaded = false;

    public static IRabbitMqBusFactoryConfigurator ConfigureInMemoryOutbox(
        this IRabbitMqBusFactoryConfigurator configurator,
        IBusRegistrationContext context,
        MassTransitOptions options)
    {
        if (!OutboxModuleWasLoaded && options.InMemoryOutbox?.Disabled != true)
        {
            configurator.UseInMemoryOutbox(context);
        }

        return configurator;
    }
}
