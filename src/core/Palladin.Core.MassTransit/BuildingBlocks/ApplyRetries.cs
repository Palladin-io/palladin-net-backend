using MassTransit;

namespace Palladin.Core.MassTransit.BuildingBlocks;

internal static class RetriesBuildingBlock
{
    internal static IRabbitMqBusFactoryConfigurator ConfigureRetries(
        this IRabbitMqBusFactoryConfigurator cfg,
        MassTransitOptions options
    )
    {
        if (options.Consumers.Retries.Disabled)
        {
            return cfg;
        }

        var retries = GetRetries(options);

        cfg.UseMessageRetry(retry => { retry.Intervals(retries); });

        return cfg;
    }

    private static TimeSpan[] GetRetries(MassTransitOptions options) =>
        options.Consumers.Retries.Intervals.Count == 0
            ? new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3) }
            : options.Consumers.Retries.Intervals.ToArray();
}
