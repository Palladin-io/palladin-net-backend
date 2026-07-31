using System.Security.Authentication;
using MassTransit;

namespace Palladin.Core.MassTransit.BuildingBlocks;

internal static class OptionsBuildingBlock
{
    internal static void ApplyOptions(this IRabbitMqHostConfigurator rabbitMqHostConfigure, RabbitMq rabbitMq)
    {
        rabbitMqHostConfigure.Username(rabbitMq.User);
        rabbitMqHostConfigure.Password(rabbitMq.Password);

        if (rabbitMq.UseAmqps)
        {
            rabbitMqHostConfigure.UseSsl(ssl => ssl.Protocol = SslProtocols.Tls12);
        }

        if (rabbitMq.PublisherConfirmation is not null)
        {
            rabbitMqHostConfigure.PublisherConfirmation = rabbitMq.PublisherConfirmation.Value;
        }

        if (rabbitMq.Heartbeat is not null)
        {
            rabbitMqHostConfigure.Heartbeat(rabbitMq.Heartbeat.Value);
        }

        if (rabbitMq.RequestedChannelMax is not null)
        {
            rabbitMqHostConfigure.RequestedChannelMax(rabbitMq.RequestedChannelMax.Value);
        }

        if (rabbitMq.RequestedConnectionTimeout is not null)
        {
            rabbitMqHostConfigure.RequestedConnectionTimeout(rabbitMq.RequestedConnectionTimeout.Value);
        }

        if (rabbitMq.ContinuationTimeout is not null)
        {
            rabbitMqHostConfigure.ContinuationTimeout(rabbitMq.ContinuationTimeout.Value);
        }
    }
}
