using JetBrains.Annotations;
using MassTransit;
using Palladin.Module.PublicAssetCatalog.Contracts.Commands;
using Palladin.Module.PublicAssetCatalog.Infrastructure.Acquisition;

namespace Palladin.Module.PublicAssetCatalog.Features;

[UsedImplicitly]
internal sealed class AcquireWebsiteIconV2ConsumerDefinition : ConsumerDefinition<AcquireWebsiteIconV2Consumer>
{
    public AcquireWebsiteIconV2ConsumerDefinition()
    {
        EndpointName = "public-asset-catalog.commands.acquire-website-icon-v2";
        ConcurrentMessageLimit = 8;
    }
}

[UsedImplicitly]
internal sealed class AcquireWebsiteIconV2Consumer(IWebsiteIconAcquirer acquirer)
    : IConsumer<AcquireWebsiteIconV2Command>
{
    public Task Consume(ConsumeContext<AcquireWebsiteIconV2Command> context) =>
        acquirer.AcquireAsync(context.Message.AssetId, context.Message.Hostname, context.CancellationToken);
}

[UsedImplicitly]
internal sealed class AcquireWebsiteIconV2FaultConsumerDefinition : ConsumerDefinition<AcquireWebsiteIconV2FaultConsumer>
{
    public AcquireWebsiteIconV2FaultConsumerDefinition()
    {
        EndpointName = "public-asset-catalog.faults.acquire-website-icon-v2";
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
