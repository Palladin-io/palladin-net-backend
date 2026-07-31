using MassTransit;
using Microsoft.Extensions.DependencyInjection;

namespace Palladin.Core.MassTransit.BuildingBlocks;

internal static class QueueTypeBuildingBlock
{
    public static void Register(IBusRegistrationConfigurator configurator, MassTransitOptions options)
    {
        switch (options.RabbitMq.QueueDefaultType)
        {
            case "quorum":
                configurator.AddTransient<IConfigureReceiveEndpoint, ConfigureQuorumReceiveEndpoint>();
                break;
        }
    }

    private sealed class ConfigureQuorumReceiveEndpoint : IConfigureReceiveEndpoint
    {
        private const string QueueType = "x-queue-type";
        private const string QuorumQueue = "quorum";

        public void Configure(string name, IReceiveEndpointConfigurator configurator)
        {
            if (configurator is IRabbitMqReceiveEndpointConfigurator rabbitMqConfigurator)
            {
                rabbitMqConfigurator.SetQueueArgument(QueueType, QuorumQueue);
            }
        }
    }
}
