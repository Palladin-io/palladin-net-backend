using JetBrains.Annotations;
using MassTransit;
using Palladin.Module.PublicAssetCatalog.Contracts.Commands;
using Palladin.Module.PublicAssetCatalog.Infrastructure.Acquisition;

namespace Palladin.Module.PublicAssetCatalog.Features;

[UsedImplicitly]
internal sealed class AcquireWebsiteIconConsumerDefinition : ConsumerDefinition<AcquireWebsiteIconConsumer>
{
    public AcquireWebsiteIconConsumerDefinition()
    {
        EndpointName = "public-asset-catalog.commands.acquire-website-icon";
        ConcurrentMessageLimit = 8;
    }
}

[UsedImplicitly]
internal sealed class AcquireWebsiteIconConsumer(IWebsiteIconAcquirer acquirer)
    : IConsumer<AcquireWebsiteIconCommand>
{
    public Task Consume(ConsumeContext<AcquireWebsiteIconCommand> context) =>
        acquirer.AcquireAsync(context.Message.Hostname, context.CancellationToken);
}
