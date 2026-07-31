using MassTransit;
using Microsoft.Extensions.Configuration;

namespace Palladin.Core.MassTransit.Plugins;

public interface IMassTransitPlugin
{
    IBusRegistrationConfigurator RegisterPlugin(
        IBusRegistrationConfigurator serviceCollection,
        IConfiguration configuration
    );

    IRegistrationContext ConfigurePlugin(
        IBusRegistrationContext context,
        IRabbitMqBusFactoryConfigurator configurator,
        MassTransitOptions options
    );
}
