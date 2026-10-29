using JetBrains.Annotations;
using MassTransit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
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
}

[UsedImplicitly]
internal sealed class AcquireWebsiteIconV2Consumer(
    IWebsiteIconAcquirer acquirer,
    IClock clock,
    IOptions<WebsiteIconAcquisitionOptions> options,
    ILogger<AcquireWebsiteIconV2Consumer> logger)
    : IConsumer<AcquireWebsiteIconV2Command>
{
    public async Task Consume(ConsumeContext<AcquireWebsiteIconV2Command> context)
    {
        WarnWhenDelayed(context);
        var result = await acquirer.AcquireAsync(
            context.Message.AssetId,
            context.Message.Hostname,
            context.CancellationToken);
        if (result != WebsiteIconAcquisitionResult.Failed)
        {
            return;
        }
        await acquirer.FailAsync(context.Message.AssetId, context.CancellationToken);
    }

    // The shared queue has no per-tenant fairness; a long wait is the observable signal of a flood.
    private void WarnWhenDelayed(ConsumeContext<AcquireWebsiteIconV2Command> context)
    {
        if (context.SentTime is not { } sentTime)
        {
            return;
        }

        var wait = clock.GetCurrentInstant() - Instant.FromDateTimeUtc(DateTime.SpecifyKind(sentTime, DateTimeKind.Utc));
        if (wait > Duration.FromSeconds(options.Value.QueueWaitWarningSeconds))
        {
            logger.LogWarning("Website icon acquisition delayed {WaitSeconds} {AssetId}", (int)wait.TotalSeconds, context.Message.AssetId);
        }
    }
}

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
