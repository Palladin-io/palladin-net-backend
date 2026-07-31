using MassTransit;

namespace Palladin.Core.MassTransit.BuildingBlocks;

internal static class KillSwitchBuildingBlock
{
    public static IRabbitMqBusFactoryConfigurator ConfigureKillSwitch(this IRabbitMqBusFactoryConfigurator configure, MassTransitOptions options)
    {
        if (options.KillSwitch.Disabled)
        {
            return configure;
        }

        configure.UseKillSwitch(
            killSwitchOptions => killSwitchOptions.SetActivationThreshold(options.KillSwitch.ActiveThresholdMessages)
                .SetRestartTimeout(options.KillSwitch.RestartAfter)
                .SetTrackingPeriod(options.KillSwitch.TrackingPeriod)
                .SetTripThreshold(options.KillSwitch.TripThreshold)
        );

        return configure;
    }
}
