using Palladin.Core.Transport;
using MassTransit;

namespace Palladin.Core.MassTransit.Filters;

public sealed class CustomHeaderSendFilter<T> : IFilter<SendContext<T>>,
                                         IFilter<PublishContext<T>>
    where T : class
{
    private readonly ITransportContext _transportContext;

    public CustomHeaderSendFilter(ITransportContext transportContext)
    {
        _transportContext = transportContext;
    }

    public void Probe(ProbeContext context)
    {
    }

    public async Task Send(PublishContext<T> context, IPipe<PublishContext<T>> next)
    {
        FillTransportContext(context);

        await next.Send(context);
    }

    public async Task Send(SendContext<T> context, IPipe<SendContext<T>> next)
    {
        FillTransportContext(context);

        await next.Send(context);
    }

    private void FillTransportContext(SendContext<T> context)
    {
        var correlationId = context.CorrelationId;
        if (correlationId is not null)
        {
            context.CorrelationId = _transportContext.CorrelationId;
        }

        foreach (var header in _transportContext.Headers.Where(x => x.Key != CustomHeaders.CorrelationIdHeaderName))
        {
            context.Headers.Set(header.Key, header.Value);
        }
    }
}
