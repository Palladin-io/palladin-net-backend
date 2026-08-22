using JetBrains.Annotations;
using MassTransit;
using Palladin.Module.PublicAssetCatalog.Contracts.Commands;
using Palladin.Module.PublicAssetCatalog.Infrastructure.Acquisition;
using Palladin.Module.PublicAssetCatalog.Infrastructure.MassTransit;

namespace Palladin.Module.PublicAssetCatalog.Features;

[UsedImplicitly]
internal sealed class AcquireWebsiteIconV2ConsumerDefinition : ConsumerDefinition<AcquireWebsiteIconV2Consumer>
{
    public AcquireWebsiteIconV2ConsumerDefinition()
    {
        EndpointName = PublicAssetCatalogEndpoints.WebsiteIcons;
        ConcurrentMessageLimit = 8;
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<AcquireWebsiteIconV2Consumer> consumerConfigurator,
        IRegistrationContext context) =>
        consumerConfigurator.UseDelayedRedelivery(retry =>
        {
            retry.Handle<WebsiteIconAcquisitionRetryRequiredException>();
            retry.Intervals(WebsiteIconAcquisitionRetryPolicy.Intervals);
        });
}

[UsedImplicitly]
internal sealed class AcquireWebsiteIconV2Consumer(IWebsiteIconAcquirer acquirer)
    : IConsumer<AcquireWebsiteIconV2Command>
{
    public async Task Consume(ConsumeContext<AcquireWebsiteIconV2Command> context)
    {
        var result = await acquirer.AcquireAsync(
            context.Message.AssetId,
            context.Message.Hostname,
            context.CancellationToken);
        if (result != WebsiteIconAcquisitionResult.RetryRequired)
        {
            return;
        }
        if (WebsiteIconAcquisitionRetryPolicy.ShouldRedeliver(context.GetRedeliveryCount()))
        {
            throw new WebsiteIconAcquisitionRetryRequiredException();
        }
        await acquirer.FailAsync(context.Message.AssetId, context.CancellationToken);
    }
}

internal static class WebsiteIconAcquisitionRetryPolicy
{
    internal static readonly TimeSpan[] Intervals =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(3),
    ];

    internal static bool ShouldRedeliver(int redeliveryCount) => redeliveryCount < Intervals.Length;
}

internal sealed class WebsiteIconAcquisitionRetryRequiredException : Exception;

[UsedImplicitly]
internal sealed class AcquireWebsiteIconV2FaultConsumerDefinition : ConsumerDefinition<AcquireWebsiteIconV2FaultConsumer>
{
    public AcquireWebsiteIconV2FaultConsumerDefinition()
    {
        EndpointName = PublicAssetCatalogEndpoints.Self;
        ConcurrentMessageLimit = 8;
    }
}

[UsedImplicitly]
internal sealed class AcquireWebsiteIconV2FaultConsumer(IWebsiteIconAcquirer acquirer)
    : IConsumer<Fault<AcquireWebsiteIconV2Command>>
{
    public Task Consume(ConsumeContext<Fault<AcquireWebsiteIconV2Command>> context) =>
        acquirer.ReleaseDispatchAsync(context.Message.Message.AssetId, context.CancellationToken);
}
