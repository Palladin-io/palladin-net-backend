using Palladin.Core.MassTransit.Filters;
using MassTransit;

namespace Palladin.Core.MassTransit.BuildingBlocks;

internal static class FilterBuildingBlock
{
    internal static IRabbitMqBusFactoryConfigurator ConfigureFilters(this IRabbitMqBusFactoryConfigurator cfg, IBusRegistrationContext context)
    {
        cfg.UseConsumeFilter(typeof(CustomHeadersConsumeFilter<>), context);
        cfg.UsePublishFilter(typeof(CustomHeaderSendFilter<>), context);
        cfg.UseSendFilter(typeof(CustomHeaderSendFilter<>), context);

        return cfg;
    }
}
