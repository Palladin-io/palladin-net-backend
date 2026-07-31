using MassTransit;

namespace Palladin.Core.MassTransit.BuildingBlocks;

public static class AddMessageLifetimeScope
{
    public static IRabbitMqBusFactoryConfigurator ConfigureMessageLifetimeScope(
        this IRabbitMqBusFactoryConfigurator configurator,
        IBusRegistrationContext context,
        MassTransitOptions options
    )
    {
        if (options.InMemoryOutbox?.Disabled != true || options.Consumers.Retries.Disabled != true)
        {
            configurator.UseMessageScope(context);
        }

        return configurator;
    }
}
